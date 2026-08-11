using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdActionReadAuthorityPort
{
    Task<NyxIdActionReadAuthorityIssueResult> IssueAsync(
        string bearerToken,
        string scopeId,
        string ownerSubject,
        string requestIdentity,
        CancellationToken ct = default);

    Task<NyxIdActionReadAuthorityResolution> ResolveAsync(
        NyxIdReadAuthorityRef? authority,
        string expectedScopeId,
        string expectedOwnerSubject,
        CancellationToken ct = default);

    Task<bool> RevokeAsync(
        NyxIdReadAuthorityRef? authority,
        string expectedScopeId,
        string expectedOwnerSubject,
        CancellationToken ct = default);
}

public sealed record NyxIdActionReadAuthorityIssueResult(
    bool Succeeded,
    NyxIdReadAuthorityRef? Authority = null,
    string? FailureCode = null);

public sealed class NyxIdActionReadAuthorityResolution
{
    private readonly AgentToolExecutionContextPayload? _transientToolContext;

    private NyxIdActionReadAuthorityResolution(
        bool resolved,
        AgentToolExecutionContextPayload? transientToolContext,
        string? failureCode)
    {
        Resolved = resolved;
        _transientToolContext = transientToolContext?.Clone();
        FailureCode = failureCode;
    }

    public bool Resolved { get; }

    public string? FailureCode { get; }

    internal AgentToolExecutionContextPayload? CloneTransientToolContext() =>
        _transientToolContext?.Clone();

    internal static NyxIdActionReadAuthorityResolution Succeeded(
        string bearerToken,
        string scopeId,
        string ownerSubject) =>
        new(
            true,
            new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload
                {
                    NyxIdAccessToken = bearerToken,
                    NyxIdCredentialKind =
                        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                },
                Caller = new AgentToolCallerContextPayload
                {
                    ScopeId = scopeId,
                    OwnerSubject = ownerSubject,
                    OwnerScopeId = scopeId,
                },
            },
            failureCode: null);

    internal static NyxIdActionReadAuthorityResolution Failed(string failureCode) =>
        new(false, transientToolContext: null, failureCode);

    public override string ToString() =>
        $"{nameof(NyxIdActionReadAuthorityResolution)} {{ Resolved = {Resolved}, FailureCode = {FailureCode ?? string.Empty} }}";
}

public sealed class NyxIdActionReadAuthorityPort : INyxIdActionReadAuthorityPort
{
    public const string MissingCode = "NYXID_ACTION_READ_AUTHORITY_MISSING";
    public const string ExpiredCode = "NYXID_ACTION_READ_AUTHORITY_EXPIRED";
    public const string RevokedCode = "NYXID_ACTION_READ_AUTHORITY_REVOKED";
    public const string ScopeMismatchCode = "NYXID_ACTION_READ_AUTHORITY_SCOPE_MISMATCH";
    public const string OwnerMismatchCode = "NYXID_ACTION_READ_AUTHORITY_OWNER_MISMATCH";
    public const string PurposeMismatchCode = "NYXID_ACTION_READ_AUTHORITY_PURPOSE_MISMATCH";
    public const string InvalidCode = "NYXID_ACTION_READ_AUTHORITY_INVALID";
    public const string UnavailableCode = "NYXID_ACTION_READ_AUTHORITY_UNAVAILABLE";

    private const string AuditIssue = "nyxid-chat-action-read-authority-issue";
    private const string AuditResolve = "nyxid-chat-action-read-authority-resolve";
    private const string AuditFence = "nyxid-chat-action-read-authority-fence";
    private const string AuditRevoke = "nyxid-chat-action-read-authority-revoke";
    private readonly ISecretVault _secretVault;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _authorityTtl;
    private readonly TimeSpan _replayWindow;

    public NyxIdActionReadAuthorityPort(
        ISecretVault secretVault,
        TimeProvider timeProvider,
        TimeSpan authorityTtl,
        TimeSpan replayWindow)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (authorityTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(authorityTtl));
        if (replayWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(replayWindow));

        _authorityTtl = authorityTtl;
        _replayWindow = replayWindow;
    }

    public async Task<NyxIdActionReadAuthorityIssueResult> IssueAsync(
        string bearerToken,
        string scopeId,
        string ownerSubject,
        string requestIdentity,
        CancellationToken ct = default)
    {
        var bearer = bearerToken ?? string.Empty;
        var scope = scopeId?.Trim() ?? string.Empty;
        var owner = ownerSubject?.Trim() ?? string.Empty;
        var request = requestIdentity?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bearer) ||
            scope.Length == 0 ||
            owner.Length == 0 ||
            request.Length == 0)
        {
            return FailedIssue(InvalidCode);
        }

        var requestedAuthorityRef = BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            scope,
            owner,
            request);
        var requestedFenceRef = BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            scope,
            owner,
            request);

        try
        {
            var fence = await ResolveVaultAsync(
                    requestedFenceRef,
                    CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
                    scope,
                    owner,
                    ct)
                .ConfigureAwait(false);
            if (fence.Resolved)
            {
                return await ResolveFencedIssueAsync(
                        fence.Secret,
                        requestedAuthorityRef,
                        scope,
                        owner,
                        ct)
                    .ConfigureAwait(false);
            }

            if (fence.FailureReason is not SecretResolutionFailureReason.NotFound)
                return FailedIssue(MapVaultFailure(fence.FailureReason));

            var existing = await ResolveAuthorityVaultAsync(requestedAuthorityRef, scope, owner, ct)
                .ConfigureAwait(false);
            if (existing.Resolved)
            {
                var existingAuthority = CreateAuthority(existing.Reference!, owner);
                await PutFenceAsync(requestedFenceRef, existingAuthority, scope, owner, ct)
                    .ConfigureAwait(false);
                return Succeeded(existingAuthority);
            }

            if (existing.FailureReason is not SecretResolutionFailureReason.NotFound)
                return FailedIssue(MapVaultFailure(existing.FailureReason));

            var stored = await _secretVault.PutAsync(
                    new StoreSecretRequest(
                        CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                        scope,
                        owner,
                        bearer,
                        AuditIssue,
                        _timeProvider.GetUtcNow().Add(_authorityTtl),
                        requestedAuthorityRef),
                    ct)
                .ConfigureAwait(false);
            var authority = CreateAuthority(stored.Reference, owner);
            await PutFenceAsync(requestedFenceRef, authority, scope, owner, ct)
                .ConfigureAwait(false);
            return Succeeded(authority);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return await ResolveRaceAsync(
                    requestedAuthorityRef,
                    requestedFenceRef,
                    scope,
                    owner,
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return FailedIssue(UnavailableCode);
        }
    }

    public async Task<NyxIdActionReadAuthorityResolution> ResolveAsync(
        NyxIdReadAuthorityRef? authority,
        string expectedScopeId,
        string expectedOwnerSubject,
        CancellationToken ct = default)
    {
        var scope = expectedScopeId?.Trim() ?? string.Empty;
        var owner = expectedOwnerSubject?.Trim() ?? string.Empty;
        var validationCode = ValidateReference(
            authority,
            scope,
            owner,
            _timeProvider.GetUtcNow(),
            checkExpiration: true);
        if (validationCode is not null)
            return FailedResolution(validationCode);

        try
        {
            var resolved = await ResolveAuthorityVaultAsync(authority!.SecretRef, scope, owner, ct)
                .ConfigureAwait(false);
            if (!resolved.Resolved || resolved.Secret is null)
                return FailedResolution(MapVaultFailure(resolved.FailureReason));
            if (!Matches(authority, resolved.Reference!))
                return FailedResolution(InvalidCode);

            return NyxIdActionReadAuthorityResolution.Succeeded(
                resolved.Secret,
                scope,
                owner);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FailedResolution(UnavailableCode);
        }
    }

    public async Task<bool> RevokeAsync(
        NyxIdReadAuthorityRef? authority,
        string expectedScopeId,
        string expectedOwnerSubject,
        CancellationToken ct = default)
    {
        var scope = expectedScopeId?.Trim() ?? string.Empty;
        var owner = expectedOwnerSubject?.Trim() ?? string.Empty;
        if (ValidateReference(
                authority,
                scope,
                owner,
                _timeProvider.GetUtcNow(),
                checkExpiration: false) is not null)
            return false;

        try
        {
            var revoked = await _secretVault.RevokeAsync(
                    new RevokeSecretRequest(
                        authority!.SecretRef,
                        CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                        scope,
                        owner,
                        AuditRevoke),
                    ct)
                .ConfigureAwait(false);
            return revoked.Revoked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<NyxIdActionReadAuthorityIssueResult> ResolveFencedIssueAsync(
        string? serializedAuthority,
        string expectedAuthorityRef,
        string scopeId,
        string ownerSubject,
        CancellationToken ct)
    {
        if (!TryParseFence(serializedAuthority, out var authority) ||
            !string.Equals(authority.SecretRef, expectedAuthorityRef, StringComparison.Ordinal))
        {
            return FailedIssue(InvalidCode);
        }

        var validationCode = ValidateReference(
            authority,
            scopeId,
            ownerSubject,
            _timeProvider.GetUtcNow(),
            checkExpiration: true);
        if (validationCode is not null)
            return FailedIssue(validationCode);

        var resolved = await ResolveAuthorityVaultAsync(authority.SecretRef, scopeId, ownerSubject, ct)
            .ConfigureAwait(false);
        if (!resolved.Resolved || resolved.Secret is null)
            return FailedIssue(MapVaultFailure(resolved.FailureReason));
        if (!Matches(authority, resolved.Reference!))
            return FailedIssue(InvalidCode);

        return Succeeded(authority);
    }

    private async Task<NyxIdActionReadAuthorityIssueResult> ResolveRaceAsync(
        string requestedAuthorityRef,
        string requestedFenceRef,
        string scopeId,
        string ownerSubject,
        CancellationToken ct)
    {
        try
        {
            var fence = await ResolveVaultAsync(
                    requestedFenceRef,
                    CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
                    scopeId,
                    ownerSubject,
                    ct)
                .ConfigureAwait(false);
            if (fence.Resolved)
            {
                return await ResolveFencedIssueAsync(
                        fence.Secret,
                        requestedAuthorityRef,
                        scopeId,
                        ownerSubject,
                        ct)
                    .ConfigureAwait(false);
            }

            var authority = await ResolveAuthorityVaultAsync(
                    requestedAuthorityRef,
                    scopeId,
                    ownerSubject,
                    ct)
                .ConfigureAwait(false);
            if (!authority.Resolved)
                return FailedIssue(MapVaultFailure(authority.FailureReason));

            var authorityRef = CreateAuthority(authority.Reference!, ownerSubject);
            await PutFenceAsync(requestedFenceRef, authorityRef, scopeId, ownerSubject, ct)
                .ConfigureAwait(false);
            return Succeeded(authorityRef);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FailedIssue(UnavailableCode);
        }
    }

    private Task<ResolveSecretResult> ResolveAuthorityVaultAsync(
        string reference,
        string scopeId,
        string ownerSubject,
        CancellationToken ct) =>
        ResolveVaultAsync(
            reference,
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            scopeId,
            ownerSubject,
            ct);

    private Task<ResolveSecretResult> ResolveVaultAsync(
        string reference,
        string purpose,
        string scopeId,
        string ownerSubject,
        CancellationToken ct) =>
        _secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                purpose,
                scopeId,
                ownerSubject,
                AuditResolve),
            ct);

    private Task<StoreSecretResult> PutFenceAsync(
        string requestedFenceRef,
        NyxIdReadAuthorityRef authority,
        string scopeId,
        string ownerSubject,
        CancellationToken ct) =>
        _secretVault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
                scopeId,
                ownerSubject,
                Convert.ToBase64String(authority.ToByteArray()),
                AuditFence,
                DateTimeOffset.FromUnixTimeMilliseconds(authority.ExpiresAtUnixMs).Add(_replayWindow),
                requestedFenceRef),
            ct);

    internal static string? ValidateReference(
        NyxIdReadAuthorityRef? authority,
        string expectedScopeId,
        string expectedOwnerSubject,
        DateTimeOffset now,
        bool checkExpiration,
        string? expectedRequestIdentity = null)
    {
        if (authority is null || string.IsNullOrWhiteSpace(authority.SecretRef))
            return MissingCode;
        if (!string.Equals(
                authority.Purpose,
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                StringComparison.Ordinal))
        {
            return PurposeMismatchCode;
        }

        if (!string.Equals(authority.ScopeId, expectedScopeId, StringComparison.Ordinal))
            return ScopeMismatchCode;
        if (!OwnerSubjectsMatch(expectedOwnerSubject, authority.OwnerSubject))
            return OwnerMismatchCode;
        if (authority.Version <= 0 || authority.ExpiresAtUnixMs <= 0)
            return InvalidCode;
        if (!string.IsNullOrWhiteSpace(expectedRequestIdentity) &&
            !string.Equals(
                authority.SecretRef,
                BuildRequestedRef(
                    CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                    expectedScopeId,
                    expectedOwnerSubject,
                    expectedRequestIdentity.Trim()),
                StringComparison.Ordinal))
        {
            return InvalidCode;
        }
        if (checkExpiration &&
            authority.ExpiresAtUnixMs <= now.ToUnixTimeMilliseconds())
        {
            return ExpiredCode;
        }

        return null;
    }

    internal static bool OwnerSubjectsMatch(
        string expectedOwnerSubject,
        string ownerSubject) =>
        string.Equals(expectedOwnerSubject, ownerSubject, StringComparison.Ordinal);

    private static bool TryParseFence(string? serializedAuthority, out NyxIdReadAuthorityRef authority)
    {
        authority = new NyxIdReadAuthorityRef();
        if (string.IsNullOrWhiteSpace(serializedAuthority))
            return false;

        try
        {
            authority = NyxIdReadAuthorityRef.Parser.ParseFrom(
                Convert.FromBase64String(serializedAuthority));
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static bool Matches(NyxIdReadAuthorityRef authority, SecretReference reference) =>
        string.Equals(authority.SecretRef, reference.Ref, StringComparison.Ordinal) &&
        string.Equals(authority.Purpose, reference.Purpose, StringComparison.Ordinal) &&
        string.Equals(authority.ScopeId, reference.OwnerScopeKey, StringComparison.Ordinal) &&
        authority.Version == reference.Version &&
        authority.ExpiresAtUnixMs == reference.ExpiresAtUnixMs;

    private static NyxIdReadAuthorityRef CreateAuthority(
        SecretReference reference,
        string ownerSubject) =>
        new()
        {
            SecretRef = reference.Ref,
            Purpose = reference.Purpose,
            ScopeId = reference.OwnerScopeKey,
            OwnerSubject = ownerSubject,
            Version = reference.Version,
            ExpiresAtUnixMs = reference.ExpiresAtUnixMs,
        };

    private static NyxIdActionReadAuthorityIssueResult Succeeded(
        NyxIdReadAuthorityRef authority) =>
        new(true, authority.Clone());

    private static NyxIdActionReadAuthorityIssueResult FailedIssue(string failureCode) =>
        new(false, FailureCode: failureCode);

    private static NyxIdActionReadAuthorityResolution FailedResolution(string failureCode) =>
        NyxIdActionReadAuthorityResolution.Failed(failureCode);

    private static string MapVaultFailure(SecretResolutionFailureReason reason) =>
        reason switch
        {
            SecretResolutionFailureReason.NotFound => MissingCode,
            SecretResolutionFailureReason.Revoked => RevokedCode,
            SecretResolutionFailureReason.Unauthorized => InvalidCode,
            _ => UnavailableCode,
        };

    internal static string BuildRequestedRef(
        string purpose,
        string scopeId,
        string ownerSubject,
        string requestIdentity)
    {
        var material = string.Join('\n', purpose, scopeId, ownerSubject, requestIdentity);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "nyxid_ref_" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
