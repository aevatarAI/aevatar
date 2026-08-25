using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdActionPostconditionPort
{
    Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext = null,
        CancellationToken ct = default);
}

/// <summary>
/// Verifies browser-reported action completion against typed NyxID catalogue
/// or exact-resource reads. It never calls a mutation API and never treats the
/// browser report as proof. Actions without sufficient authority, freshness,
/// identity, or version evidence fail closed.
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
    public const string LineageUnavailableCode =
        "NYXID_ACTION_POSTCONDITION_ROTATION_LINEAGE_UNAVAILABLE";

    private readonly INyxIdAuthorizationCatalogQueryPort? _catalogQueryPort;
    private readonly INyxIdActionEvidenceReadPort? _actionEvidenceReadPort;
    private readonly TimeProvider _timeProvider;

    public NyxIdActionPostconditionPort(
        INyxIdAuthorizationCatalogQueryPort catalogQueryPort,
        TimeProvider timeProvider)
        : this(catalogQueryPort, null, timeProvider)
    {
    }

    public NyxIdActionPostconditionPort(
        INyxIdAuthorizationCatalogQueryPort? catalogQueryPort,
        INyxIdActionEvidenceReadPort? actionEvidenceReadPort,
        TimeProvider timeProvider)
    {
        _catalogQueryPort = catalogQueryPort;
        _actionEvidenceReadPort = actionEvidenceReadPort;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext = null,
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
            NyxIdAssistantActionKind.ServiceAccessReview =>
                await VerifyServiceAccessReviewAsync(input, transientToolContext, ct)
                    .ConfigureAwait(false),
            NyxIdAssistantActionKind.ServiceReauthorize =>
                await VerifyServiceReauthorizeAsync(input, transientToolContext, ct)
                    .ConfigureAwait(false),
            NyxIdAssistantActionKind.KeyCreate =>
                await VerifyKeyCreateAsync(input, transientToolContext, ct).ConfigureAwait(false),
            NyxIdAssistantActionKind.KeyRotate =>
                await VerifyKeyRotateAsync(input, transientToolContext, ct).ConfigureAwait(false),
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
        var invalid = ValidateSnapshot(input, snapshot, exactHint);
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

    private async Task<NyxIdChatActionPostconditionResult> VerifyServiceAccessReviewAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase !=
                NyxIdAssistantActionParams.ParamsOneofCase.ServiceAccessReview ||
            !ValidIdentity(input.Params.ServiceAccessReview.UserServiceId) ||
            !ValidServiceSlug(input.Params.ServiceAccessReview.ServiceSlug) ||
            !ValidServiceResourceUri(
                input.Params.ServiceAccessReview.ResourceUri,
                input.Params.ServiceAccessReview.ServiceSlug))
        {
            return Unverified(input, InvalidInputCode, "The service-access params are invalid.");
        }

        var expectedUserServiceId = input.Params.ServiceAccessReview.UserServiceId;
        if (input.ResourceHint is not null &&
            input.ResourceHint.ResourceCase is not
                (NyxIdChatSafeResourceRef.ResourceOneofCase.None or
                 NyxIdChatSafeResourceRef.ResourceOneofCase.UserService))
        {
            return Unverified(
                input,
                MismatchCode,
                "The service-access report referenced the wrong resource kind.");
        }

        var exactHint = ResolveUserServiceHint(input.ResourceHint);
        if (exactHint is not null && !string.Equals(
                exactHint,
                expectedUserServiceId,
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "The connected-service reference did not match the requested access review.");
        }

        var bearerToken = ResolveProviderBearerToken(
            input,
            transientToolContext,
            out var contextFailure);
        if (contextFailure is not null)
            return contextFailure;
        if (_actionEvidenceReadPort is null)
            return ProviderReadUnavailable(input);

        var expectedServiceSlug = input.Params.ServiceAccessReview.ServiceSlug;
        var read = await _actionEvidenceReadPort.GetServiceAccessAsync(
                bearerToken!,
                expectedUserServiceId,
                expectedServiceSlug,
                ct)
            .ConfigureAwait(false);
        if (!read.Succeeded)
            return ProviderReadFailure(input, read.Failure);

        var evidence = read.Value!;
        if (!string.Equals(
                evidence.UserServiceId,
                expectedUserServiceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ServiceSlug,
                expectedServiceSlug,
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "The exact NyxID service access did not match the requested review.");
        }

        return Verified(
            input,
            new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = evidence.UserServiceId,
                },
            });
    }

    private async Task<NyxIdChatActionPostconditionResult> VerifyServiceReauthorizeAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase !=
                NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize ||
            !ValidIdentity(input.Params.ServiceReauthorize.UserServiceId) ||
            !ValidNormalizedSet(input.Params.ServiceReauthorize.RequestedScopes, requireAny: true) ||
            !TryResolveRequestedAt(input, out var requestedAt))
        {
            return Unverified(input, InvalidInputCode, "The service-reauthorize params are invalid.");
        }

        var expectedUserServiceId = input.Params.ServiceReauthorize.UserServiceId;
        if (input.ResourceHint is not null &&
            input.ResourceHint.ResourceCase is not
                (NyxIdChatSafeResourceRef.ResourceOneofCase.None or
                 NyxIdChatSafeResourceRef.ResourceOneofCase.UserService))
        {
            return Unverified(
                input,
                MismatchCode,
                "The reauthorization report referenced the wrong resource kind.");
        }

        var exactHint = ResolveUserServiceHint(input.ResourceHint);
        if (exactHint is not null && !string.Equals(
                exactHint,
                expectedUserServiceId,
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "The connected-service reference did not match the requested reauthorization.");
        }

        var bearerToken = ResolveProviderBearerToken(input, transientToolContext, out var contextFailure);
        if (contextFailure is not null)
            return contextFailure;
        if (_actionEvidenceReadPort is null)
            return ProviderReadUnavailable(input);

        var read = await _actionEvidenceReadPort.GetUserServiceAuthorizationAsync(
                bearerToken!,
                expectedUserServiceId,
                ct)
            .ConfigureAwait(false);
        if (!read.Succeeded)
            return ProviderReadFailure(input, read.Failure);

        var evidence = read.Value!;
        if (!string.Equals(evidence.UserServiceId, expectedUserServiceId, StringComparison.Ordinal) ||
            !evidence.IsActive ||
            evidence.CredentialStatus != NyxIdUserServiceCredentialStatus.Active ||
            evidence.OAuthConnectionStatus != NyxIdOAuthConnectionStatus.Active ||
            evidence.GrantedScopes is null ||
            !input.Params.ServiceReauthorize.RequestedScopes.All(scope =>
                evidence.GrantedScopes.Contains(scope, StringComparer.Ordinal)))
        {
            return Unverified(
                input,
                MismatchCode,
                "The connected-service authorization did not match the requested scopes.");
        }

        if (evidence.LastAuthorizedAtUtc is null ||
            evidence.LastAuthorizedAtUtc < requestedAt ||
            evidence.LastAuthorizedAtUtc > _timeProvider.GetUtcNow())
        {
            return Unverified(
                input,
                StaleCode,
                "The connected-service authorization timestamp was unavailable or stale.");
        }

        return Verified(
            input,
            new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = evidence.UserServiceId,
                },
            });
    }

    private async Task<NyxIdChatActionPostconditionResult> VerifyKeyCreateAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase != NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate ||
            !ValidDisplayValue(input.Params.KeyCreate.Name) ||
            !ValidIdentity(input.Params.KeyCreate.Platform) ||
            !ValidNormalizedSet(input.Params.KeyCreate.AllowedServiceIds, requireAny: true) ||
            !TryResolveRequestedAt(input, out var requestedAt))
        {
            return Unverified(input, InvalidInputCode, "The key-create params are invalid.");
        }

        var keyId = ResolveExactKeyHint(input.ResourceHint);
        if (keyId is null)
        {
            return Unverified(
                input,
                MismatchCode,
                "An exact key reference is required to verify key creation.");
        }

        var bearerToken = ResolveProviderBearerToken(input, transientToolContext, out var contextFailure);
        if (contextFailure is not null)
            return contextFailure;
        if (_actionEvidenceReadPort is null)
            return ProviderReadUnavailable(input);

        var read = await _actionEvidenceReadPort.GetAgentApiKeyAsync(
                bearerToken!,
                keyId,
                ct)
            .ConfigureAwait(false);
        if (!read.Succeeded)
            return ProviderReadFailure(input, read.Failure);

        // The display name is not compared: it is user-controlled text, not
        // evidence. Identity, platform, scopes, and the exact service set are
        // the typed postcondition.
        var evidence = read.Value!;
        if (!evidence.IsActive ||
            !string.Equals(evidence.Id, keyId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Platform, input.Params.KeyCreate.Platform, StringComparison.Ordinal) ||
            !SetEquals(evidence.Scopes, ["proxy"]) ||
            evidence.AllowAllServices ||
            !SetEquals(evidence.AllowedServiceIds, input.Params.KeyCreate.AllowedServiceIds) ||
            evidence.AllowAllNodes ||
            evidence.AllowedNodeIds.Count != 0)
        {
            return Unverified(
                input,
                MismatchCode,
                "The exact NyxID key did not match the requested key creation.");
        }

        if (evidence.CreatedAtUtc < requestedAt ||
            evidence.CreatedAtUtc > _timeProvider.GetUtcNow())
        {
            return Unverified(input, StaleCode, "The NyxID key timestamp was invalid.");
        }

        return Verified(input, KeyResource(evidence.Id));
    }

    private async Task<NyxIdChatActionPostconditionResult> VerifyKeyRotateAsync(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct)
    {
        if (input.Params?.ParamsCase != NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate ||
            !ValidIdentity(input.Params.KeyRotate.KeyId) ||
            !TryResolveRequestedAt(input, out var requestedAt))
        {
            return Unverified(input, InvalidInputCode, "The key-rotate params are invalid.");
        }

        var newKeyId = ResolveExactKeyHint(input.ResourceHint);
        if (newKeyId is null || string.Equals(
                newKeyId,
                input.Params.KeyRotate.KeyId,
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "An exact replacement key reference is required to verify key rotation.");
        }

        var bearerToken = ResolveProviderBearerToken(input, transientToolContext, out var contextFailure);
        if (contextFailure is not null)
            return contextFailure;
        if (_actionEvidenceReadPort is null)
            return ProviderReadUnavailable(input);

        var read = await _actionEvidenceReadPort.GetAgentApiKeyAsync(
                bearerToken!,
                newKeyId,
                ct)
            .ConfigureAwait(false);
        if (!read.Succeeded)
            return ProviderReadFailure(input, read.Failure);

        var evidence = read.Value!;
        if (!evidence.IsActive || !string.Equals(evidence.Id, newKeyId, StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "The replacement NyxID key did not match the reported resource.");
        }

        var versionEvidence = evidence.VersionEvidence;
        if (versionEvidence is null ||
            versionEvidence.RotationPredecessorId is not { } predecessorId ||
            !ValidIdentity(predecessorId) ||
            versionEvidence.StateVersion <= 0)
        {
            return Unverified(
                input,
                LineageUnavailableCode,
                "NyxID did not expose authoritative key rotation lineage.");
        }

        if (!string.Equals(
                predecessorId,
                input.Params.KeyRotate.KeyId,
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                MismatchCode,
                "The NyxID key rotation lineage did not match the requested key.");
        }

        // The monotonic state_version and predecessor lineage above are the
        // authoritative rotation evidence; the update timestamp is a freshness
        // fence applied only when the projection serializes one.
        var now = _timeProvider.GetUtcNow();
        if (evidence.CreatedAtUtc < requestedAt ||
            evidence.CreatedAtUtc > now ||
            versionEvidence.UpdatedAtUtc is { } versionUpdatedAt &&
            (versionUpdatedAt < requestedAt || versionUpdatedAt > now))
        {
            return Unverified(input, StaleCode, "The NyxID key rotation version was stale.");
        }

        return Verified(input, KeyResource(evidence.Id));
    }

    private async Task<NyxIdAuthorizationCatalogSnapshot?> ReadCatalogAsync(
        string ownerSubject,
        CancellationToken ct)
    {
        if (_catalogQueryPort is null)
            return null;
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
        NyxIdAuthorizationCatalogSnapshot? snapshot,
        string? requiredUserServiceId = null)
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
                    snapshot.Services,
                    snapshot.GatewayLLMTarget),
                StringComparison.Ordinal))
        {
            return Unverified(
                input,
                UnavailableCode,
                "The NyxID action postcondition read model is invalid.");
        }

        var now = _timeProvider.GetUtcNow();
        if (requiredUserServiceId is not null)
        {
            var matches = snapshot.Services
                .Where(service => string.Equals(
                    service.UserServiceId,
                    requiredUserServiceId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length == 1 &&
                (!NyxIdAuthorizationCatalogIntegrity.TryResolveServiceAuthorityWindow(
                     snapshot,
                     matches[0],
                     out var observedAtUtc,
                     out var freshUntilUtc) ||
                 observedAtUtc > now ||
                 freshUntilUtc <= now))
            {
                return Unverified(
                    input,
                    StaleCode,
                    "The NyxID action postcondition read model is stale.");
            }

            return null;
        }

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

    private static string? ResolveProviderBearerToken(
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        out NyxIdChatActionPostconditionResult? failure)
    {
        failure = null;
        if (transientToolContext is null)
        {
            failure = ProviderReadUnavailable(input);
            return null;
        }

        var context = AgentToolExecutionContextMapper.FromPayload(transientToolContext);
        if (!string.Equals(context.Caller.ScopeId, input.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(context.Caller.OwnerSubject, input.OwnerSubject, StringComparison.Ordinal))
        {
            failure = Unverified(
                input,
                MismatchCode,
                "The transient NyxID read authority did not match the action owner.");
            return null;
        }

        var bearerToken = AgentToolHumanSessionNyxIdCredential.ResolveBearerToken(context);
        if (bearerToken is null)
            failure = ProviderReadUnavailable(input);
        return bearerToken;
    }

    private static NyxIdChatActionPostconditionResult ProviderReadFailure(
        NyxIdChatActionPostconditionInput input,
        NyxIdApiAccessFailure? failure) =>
        failure?.Kind == NyxIdApiAccessFailureKind.NotFound
            ? Unverified(
                input,
                MismatchCode,
                "The exact NyxID resource was not found.")
            : ProviderReadUnavailable(input);

    private static NyxIdChatActionPostconditionResult ProviderReadUnavailable(
        NyxIdChatActionPostconditionInput input) =>
        Unverified(
            input,
            UnavailableCode,
            "The exact NyxID provider read was unavailable.");

    private static NyxIdChatSafeResourceRef KeyResource(string keyId) => new()
    {
        Key = new NyxIdChatKeyRef { KeyId = keyId },
    };

    private static string? ResolveExactKeyHint(NyxIdChatSafeResourceRef? resource) =>
        resource?.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.Key &&
        ValidIdentity(resource.Key.KeyId)
            ? resource.Key.KeyId
            : null;

    private static bool ValidNormalizedSet(
        IEnumerable<string> values,
        bool requireAny)
    {
        var items = values.ToArray();
        return (!requireAny || items.Length > 0) &&
               items.All(ValidIdentity) &&
               items.Distinct(StringComparer.Ordinal).Count() == items.Length;
    }

    private static bool SetEquals(
        IEnumerable<string> left,
        IEnumerable<string> right) =>
        new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

    private static bool ValidDisplayValue(string value) =>
        ValidIdentity(value) && value.Length <= 128;

    private static bool ValidServiceSlug(string value) =>
        ValidIdentity(value) &&
        value.Length <= 128 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ValidServiceResourceUri(string value, string serviceSlug)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var expectedPathSuffix = $"/api/v1/proxy/s/{Uri.EscapeDataString(serviceSlug)}";
        return uri.AbsolutePath.EndsWith(expectedPathSuffix, StringComparison.Ordinal);
    }

    private static bool TryResolveRequestedAt(
        NyxIdChatActionPostconditionInput input,
        out DateTimeOffset requestedAt)
    {
        requestedAt = default;
        if (input.RequestedAt is null)
            return false;

        try
        {
            requestedAt = input.RequestedAt.ToDateTimeOffset();
            return requestedAt > DateTimeOffset.UnixEpoch;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
        AgentToolExecutionContextPayload? transientToolContext = null,
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
