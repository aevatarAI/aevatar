using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdActionPostconditionPort
{
    Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdChatActionPostconditionInput input,
        CancellationToken ct = default);
}

/// <summary>
/// Verifies browser-reported action completion against typed, durable NyxID
/// read models. It never calls a mutation API and never treats the report as
/// proof. V1 currently has an authoritative catalog read for connected
/// services; other action kinds fail closed until their own typed read ports
/// are composed.
/// </summary>
public sealed class NyxIdActionPostconditionPort : INyxIdActionPostconditionPort
{
    public const string VerifiedCode = "NYXID_ACTION_POSTCONDITION_VERIFIED";
    public const string InvalidInputCode = "NYXID_ACTION_POSTCONDITION_INPUT_INVALID";
    public const string UnavailableCode = "NYXID_ACTION_POSTCONDITION_UNAVAILABLE";
    public const string StaleCode = "NYXID_ACTION_POSTCONDITION_STALE";
    public const string MismatchCode = "NYXID_ACTION_POSTCONDITION_MISMATCH";
    public const string AmbiguousCode = "NYXID_ACTION_POSTCONDITION_AMBIGUOUS";
    public const string UnsupportedCode = "NYXID_ACTION_POSTCONDITION_UNSUPPORTED";

    private readonly INyxIdAuthorizationCatalogQueryPort _catalogQueryPort;
    private readonly TimeProvider _timeProvider;

    public NyxIdActionPostconditionPort(
        INyxIdAuthorizationCatalogQueryPort catalogQueryPort,
        TimeProvider timeProvider)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdChatActionPostconditionInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        if (!IsValidCommonInput(input))
            return Unverified(input, InvalidInputCode, "The action postcondition input is invalid.");

        return input.Action switch
        {
            NyxIdAssistantActionKind.ServiceConnect =>
                await VerifyServiceConnectAsync(input, ct).ConfigureAwait(false),
            NyxIdAssistantActionKind.ServiceReauthorize =>
                await VerifyServiceReauthorizeAsync(input, ct).ConfigureAwait(false),
            _ => Unverified(
                input,
                UnsupportedCode,
                "No typed read model is configured for this action postcondition."),
        };
    }

    private async Task<NyxIdChatActionPostconditionResult> VerifyServiceConnectAsync(
        NyxIdChatActionPostconditionInput input,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase is not
                (NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect or
                 NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect))
        {
            return Unverified(input, InvalidInputCode, "The service-connect params are invalid.");
        }

        var exactHint = ResolveUserServiceHint(input.ResourceHint);
        if (input.Params.ParamsCase ==
                NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect &&
            exactHint is null)
        {
            return Unverified(
                input,
                MismatchCode,
                "An exact connected-service reference is required to verify a custom service connection.");
        }

        var snapshot = await ReadCatalogAsync(input.OwnerSubject, ct).ConfigureAwait(false);
        var invalid = ValidateSnapshot(input, snapshot);
        if (invalid is not null)
            return invalid;

        var matches = snapshot!.Services
            .Where(static service =>
                service.Access == NyxIdAuthorizationAccess.Permitted &&
                !string.IsNullOrWhiteSpace(service.UserServiceId))
            .Where(service => exactHint is null || string.Equals(
                service.UserServiceId,
                exactHint,
                StringComparison.Ordinal))
            .Where(service => ServiceConnectParamsMatch(input.Params, service))
            .ToArray();

        if (matches.Length == 0)
        {
            return Unverified(
                input,
                MismatchCode,
                "The connected-service read model did not match the requested action.");
        }

        if (matches.Length > 1)
        {
            return Unverified(
                input,
                AmbiguousCode,
                "The connected-service read model did not identify one exact resource.");
        }

        return Verified(
            input,
            new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = matches[0].UserServiceId,
                },
            });
    }

    private async Task<NyxIdChatActionPostconditionResult> VerifyServiceReauthorizeAsync(
        NyxIdChatActionPostconditionInput input,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase !=
                NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize ||
            string.IsNullOrWhiteSpace(input.Params.ServiceReauthorize.KeyId))
        {
            return Unverified(input, InvalidInputCode, "The service-reauthorize params are invalid.");
        }

        var exactHint = ResolveUserServiceHint(input.ResourceHint);
        if (exactHint is null)
        {
            return Unverified(
                input,
                MismatchCode,
                "An exact connected-service reference is required to verify reauthorization.");
        }

        var snapshot = await ReadCatalogAsync(input.OwnerSubject, ct).ConfigureAwait(false);
        var invalid = ValidateSnapshot(input, snapshot);
        if (invalid is not null)
            return invalid;

        var match = snapshot!.Services.SingleOrDefault(service =>
            service.Access == NyxIdAuthorizationAccess.Permitted &&
            string.Equals(service.UserServiceId, exactHint, StringComparison.Ordinal));
        return match is null
            ? Unverified(
                input,
                MismatchCode,
                "The reauthorized service was not visible in the typed read model.")
            : Verified(input, input.ResourceHint.Clone());
    }

    private async Task<NyxIdAuthorizationCatalogSnapshot?> ReadCatalogAsync(
        string ownerSubject,
        CancellationToken ct)
    {
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = ownerSubject.Trim(),
        };
        return await _catalogQueryPort.GetAsync(owner, ct).ConfigureAwait(false);
    }

    private NyxIdChatActionPostconditionResult? ValidateSnapshot(
        NyxIdChatActionPostconditionInput input,
        NyxIdAuthorizationCatalogSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Unverified(
                input,
                UnavailableCode,
                "The NyxID action postcondition read model is unavailable.");
        }

        var expectedOwner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = input.OwnerSubject.Trim(),
        };
        if (!OwnerEquals(snapshot.Owner, expectedOwner) ||
            snapshot.StateVersion <= 0 ||
            !snapshot.Activated ||
            snapshot.Invalidated ||
            snapshot.Cleaned ||
            string.IsNullOrWhiteSpace(snapshot.ContentDigest) ||
            !string.Equals(
                snapshot.ContentDigest,
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                    snapshot.Owner,
                    snapshot.Services),
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                UnavailableCode,
                "The NyxID action postcondition read model is invalid.");
        }

        var now = _timeProvider.GetUtcNow();
        if (snapshot.ObservedAtUtc == default ||
            snapshot.ObservedAtUtc > now ||
            snapshot.FreshUntilUtc <= now)
        {
            return Unverified(
                input,
                StaleCode,
                "The NyxID action postcondition read model is stale.");
        }

        return null;
    }

    private static bool IsValidCommonInput(NyxIdChatActionPostconditionInput input) =>
        input.ReportedDisposition is
            NyxIdChatActionDisposition.Completed or
            NyxIdChatActionDisposition.Unspecified &&
        !string.IsNullOrWhiteSpace(input.ScopeId) &&
        !string.IsNullOrWhiteSpace(input.OwnerSubject) &&
        !string.IsNullOrWhiteSpace(input.OriginTurnId) &&
        !string.IsNullOrWhiteSpace(input.ActionRequestId) &&
        input.Action != NyxIdAssistantActionKind.Unspecified &&
        input.Params?.ParamsCase != NyxIdAssistantActionParams.ParamsOneofCase.None &&
        ValidResourceHint(input.ResourceHint);

    private static bool ServiceConnectParamsMatch(
        NyxIdAssistantActionParams actionParams,
        NyxIdAuthorizationServiceEvidence service) =>
        actionParams.ParamsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                !string.IsNullOrWhiteSpace(actionParams.CatalogServiceConnect.ServiceSlug) &&
                string.Equals(
                    service.ServiceSlug,
                    actionParams.CatalogServiceConnect.ServiceSlug,
                    StringComparison.Ordinal),
            // Custom endpoint identity cannot be inferred from display text or a
            // URL. It therefore requires an exact typed resource hint and only
            // proves that exact visible service exists.
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect => true,
            _ => false,
        };

    private static string? ResolveUserServiceHint(NyxIdChatSafeResourceRef? resource) =>
        resource?.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.UserService &&
        !string.IsNullOrWhiteSpace(resource.UserService.UserServiceId)
            ? resource.UserService.UserServiceId.Trim()
            : null;

    private static bool ValidResourceHint(NyxIdChatSafeResourceRef? resource) =>
        resource is null || resource.ResourceCase switch
        {
            NyxIdChatSafeResourceRef.ResourceOneofCase.None => true,
            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService =>
                ValidIdentity(resource.UserService.UserServiceId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Key =>
                ValidIdentity(resource.Key.KeyId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Node =>
                ValidIdentity(resource.Node.NodeId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.ServiceAccount =>
                ValidIdentity(resource.ServiceAccount.ServiceAccountId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.DeveloperApp =>
                ValidIdentity(resource.DeveloperApp.ClientId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Device =>
                ValidIdentity(resource.Device.DeviceId),
            _ => false,
        };

    private static bool ValidIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool OwnerEquals(
        AuthorizationOwnerIdentity? left,
        AuthorizationOwnerIdentity right) =>
        left is not null &&
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private static NyxIdChatActionPostconditionResult Verified(
        NyxIdChatActionPostconditionInput input,
        NyxIdChatSafeResourceRef resource) =>
        new()
        {
            ActionRequestId = input.ActionRequestId,
            Disposition = NyxIdChatActionDisposition.Completed,
            Verified = true,
            Resource = resource,
            FailureCode = string.Empty,
            SafeMessage = string.Empty,
        };

    private static NyxIdChatActionPostconditionResult Unverified(
        NyxIdChatActionPostconditionInput input,
        string code,
        string safeMessage) =>
        new()
        {
            ActionRequestId = input.ActionRequestId?.Trim() ?? string.Empty,
            Disposition = input.ReportedDisposition,
            Verified = false,
            Resource = input.ResourceHint?.Clone(),
            FailureCode = code,
            SafeMessage = safeMessage,
        };
}

internal sealed class UnavailableNyxIdActionPostconditionPort
    : INyxIdActionPostconditionPort
{
    public Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdChatActionPostconditionInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new NyxIdChatActionPostconditionResult
        {
            ActionRequestId = input.ActionRequestId,
            Disposition = input.ReportedDisposition,
            Verified = false,
            Resource = input.ResourceHint?.Clone(),
            FailureCode = NyxIdActionPostconditionPort.UnavailableCode,
            SafeMessage = "The NyxID action postcondition read model is unavailable.",
        });
    }
}
