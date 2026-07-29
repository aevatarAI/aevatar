using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

internal sealed class ScheduledAgentApiKeyIssuer : IScheduledAgentApiKeyIssuer
{
    private static readonly JsonSerializerOptions CreateKeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly ILogger<ScheduledAgentApiKeyIssuer>? _logger;
    private readonly TimeProvider _timeProvider;

    public ScheduledAgentApiKeyIssuer(
        INyxIdApiClientFactory nyxClientFactory,
        ILogger<ScheduledAgentApiKeyIssuer>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScheduledAgentApiKeyRevokeResult> RevokeActiveKeysByNameAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct)
    {
        var lookup = await FindActiveKeysByNameAsync(token, validatedPlan, credentialName, ct);
        if (!lookup.Completed)
        {
            return ScheduledAgentApiKeyRevokeResult.Pending(
                lookup.HttpStatus,
                lookup.Error,
                lookup.FailureKind);
        }

        ScheduledAgentApiKeyRevokeResult? firstFailure = null;
        foreach (var apiKeyId in lookup.ActiveApiKeyIds)
        {
            ScheduledAgentApiKeyRevokeResult result;
            try
            {
                result = await RevokeAsync(token, apiKeyId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "NyxID stale API key revocation failed during reconciliation.");
                result = ScheduledAgentApiKeyRevokeResult.Pending(
                    0,
                    "nyxid_api_key_revoke_failed",
                    UserAgentApiKeyRevocationFailureKind.Transient);
            }

            if (!result.Completed)
                firstFailure ??= result;
        }

        return firstFailure ?? ScheduledAgentApiKeyRevokeResult.Complete();
    }

    public async Task<ScheduledAgentApiKeyLookupResult> FindActiveKeysByNameAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ScheduledAgentApiKeyLookupResult.Pending(
                0,
                "missing_access_token",
                UserAgentApiKeyRevocationFailureKind.Unauthorized);
        }
        ArgumentNullException.ThrowIfNull(validatedPlan);
        var exactCredentialName = Normalize(credentialName);
        if (exactCredentialName == null)
        {
            return ScheduledAgentApiKeyLookupResult.Pending(
                0,
                "missing_credential_name",
                UserAgentApiKeyRevocationFailureKind.ProviderError);
        }
        if (!validatedPlan.HasValidIntegrity ||
            !TryResolveOwnerScope(
                validatedPlan.Plan?.Owner,
                out var ownerKind,
                out var ownerSubject))
        {
            return ScheduledAgentApiKeyLookupResult.Pending(
                0,
                "authorization_plan_owner_invalid",
                UserAgentApiKeyRevocationFailureKind.Unauthorized);
        }

        string response;
        try
        {
            var client = _nyxClientFactory.CreateClient();
            response = ownerKind == AuthorizationOwnerKind.Organization
                ? await client.ListApiKeysAsync(token, ownerSubject, ct)
                : await client.ListApiKeysAsync(token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NyxID API key reconciliation list failed.");
            return ScheduledAgentApiKeyLookupResult.Pending(
                0,
                "nyxid_api_key_list_failed",
                UserAgentApiKeyRevocationFailureKind.Transient);
        }

        if (TryReadErrorEnvelope(response, out var status, out var body, out var message))
        {
            return ScheduledAgentApiKeyLookupResult.Pending(
                status ?? 0,
                Normalize(body) ?? Normalize(message) ?? "nyxid_api_key_list_failed",
                ClassifyRevocationFailure(status));
        }
        if (!TryReadMatchingActiveApiKeyIds(response, exactCredentialName, out var apiKeyIds))
        {
            return ScheduledAgentApiKeyLookupResult.Pending(
                0,
                "nyxid_api_key_list_malformed",
                UserAgentApiKeyRevocationFailureKind.ProviderError);
        }

        return ScheduledAgentApiKeyLookupResult.Complete(apiKeyIds);
    }

    public async Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(validatedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        if (!validatedPlan.HasValidIntegrity)
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_integrity_invalid");
        var plan = validatedPlan.Plan ?? throw new ArgumentException("Validated authorization plan is missing.", nameof(validatedPlan));
        var policy = plan.CredentialPolicy;
        var owner = plan.Owner;
        if (policy == null ||
            policy.AllowAllServices ||
            policy.AllowAllNodes ||
            policy.ServiceGrantRequirement is not (AuthorizationGrantRequirement.NotRequired or
                AuthorizationGrantRequirement.Required) ||
            policy.NodeGrantRequirement is not (AuthorizationGrantRequirement.NotRequired or
                AuthorizationGrantRequirement.Required) ||
            owner == null ||
            !string.Equals(owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) ||
            !IsNormalized(owner.OwnerSubject) ||
            !TryResolveOwnerScope(owner, out var ownerKind, out var ownerSubject) ||
            !IsValidAuthenticatedActor(plan.AuthenticatedActor) ||
            !TryResolveFutureExpiry(policy.ExpiresAt, _timeProvider.GetUtcNow(), out var expiresAt))
        {
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_policy_invalid");
        }

        var serviceIds = plan.NyxIdServiceGrants
            .Select(static grant => grant.UserServiceId)
            .ToArray();
        var nodeIds = plan.NyxIdServiceGrants
            .SelectMany(static grant => grant.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var serviceCatalogRequired = serviceIds.Length > 0 ||
                                     policy.ServiceGrantRequirement == AuthorizationGrantRequirement.Required;
        if (!IsCanonicalIdSequence(serviceIds) ||
            plan.NyxIdServiceGrants.Any(static grant => !IsValidServiceGrant(grant)) ||
            serviceCatalogRequired && (plan.CatalogAuthority == null ||
                !IsNormalized(plan.CatalogAuthority.ContractVersion) ||
                !IsNormalized(plan.CatalogAuthority.PolicyVersion)) ||
            serviceIds.Length == 0 && policy.ServiceGrantRequirement == AuthorizationGrantRequirement.Required ||
            nodeIds.Length == 0 && policy.NodeGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_grants_invalid");
        }
        var scopes = ResolveScopes(policy.Scopes);
        if (scopes == null)
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_scopes_invalid");

        var targetOrganizationId = ownerKind == AuthorizationOwnerKind.Organization
            ? ownerSubject
            : null;
        var client = _nyxClientFactory.CreateClient();
        string scopePlanResponse;
        try
        {
            scopePlanResponse = await client.PlanApiKeyScopeAsync(
                token,
                serviceIds,
                targetOrganizationId,
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("nyxid_scope_plan_provider_timed_out");
        }
        var scopePlanResult = NyxIdApiAccessResponseParser.ParseScopePlan(scopePlanResponse);
        if (!scopePlanResult.Succeeded)
        {
            var failure = scopePlanResult.Failure;
            return ScheduledAgentApiKeyIssueResult.Failed(
                failure?.Code ?? "nyxid_scope_plan_failed",
                httpStatus: failure?.HttpStatus);
        }

        var scopePlan = scopePlanResult.Value!;
        if (!TryResolveAuthorizationPlanMismatch(
                scopePlan,
                plan,
                ownerKind,
                ownerSubject,
                serviceIds,
                nodeIds,
                out var mismatchReason))
        {
            if (_logger != null)
            {
                _logger.Log(
                    LogLevel.Warning,
                    new EventId(),
                    $"NyxID scheduled API key scope-plan no longer matches the validated authorization plan. reason={ScheduledAuthorizationPlanMismatchReasons.ToWireValue(mismatchReason)} planned_service_count={plan.NyxIdServiceGrants.Count()} current_service_count={scopePlan.Services.Count()} planned_node_count={nodeIds.Count()} current_node_count={scopePlan.AllowedNodeIds.Count()}",
                    null,
                    static (state, _) => state);
            }
            return ScheduledAgentApiKeyIssueResult.Failed(
                "authorization_plan_changed",
                authorizationPlanMismatchReason: mismatchReason);
        }

        var response = await client.CreateApiKeyAsync(
            token,
            JsonSerializer.Serialize(new
            {
                name = credentialName.Trim(),
                scopes,
                platform = "generic",
                allow_all_services = false,
                allow_all_nodes = false,
                allowed_service_ids = scopePlan.AllowedServiceIds,
                allowed_node_ids = scopePlan.AllowedNodeIds,
                scope_plan_digest = scopePlan.NormalizedGrantDigest,
                target_org_id = targetOrganizationId,
                expires_at = expiresAt.ToString("O"),
            }, CreateKeyJsonOptions),
            ct);

        return ExtractIssuedKey(response, expiresAt.ToUnixTimeMilliseconds());
    }

    public async Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(string token, string apiKeyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ScheduledAgentApiKeyRevokeResult.Pending(
                0,
                "missing_access_token",
                UserAgentApiKeyRevocationFailureKind.Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(apiKeyId))
        {
            return ScheduledAgentApiKeyRevokeResult.Pending(
                0,
                "missing_api_key_id",
                UserAgentApiKeyRevocationFailureKind.ProviderError);
        }

        var response = await _nyxClientFactory.CreateClient().DeleteApiKeyAsync(token, apiKeyId.Trim(), ct);
        if (!TryReadErrorEnvelope(response, out var status, out var body, out var message))
            return ScheduledAgentApiKeyRevokeResult.Complete();
        if (status == 404)
            return ScheduledAgentApiKeyRevokeResult.Complete(404);

        return ScheduledAgentApiKeyRevokeResult.Pending(
            status ?? 0,
            Normalize(body) ?? Normalize(message) ?? "nyxid_api_key_revoke_failed",
            ClassifyRevocationFailure(status));
    }

    private static ScheduledAgentApiKeyIssueResult ExtractIssuedKey(string response, long keyExpiresAtUnixMs)
    {
        if (TryReadErrorEnvelope(response, out var status, out var body, out var message))
        {
            var detailSuffix = string.IsNullOrWhiteSpace(body) ? message : body;
            var detail = "NyxID rejected the scheduled-agent API key creation" +
                         (status.HasValue ? $" with HTTP {status.Value}" : string.Empty) +
                         (string.IsNullOrWhiteSpace(detailSuffix) ? "." : $". Response: {detailSuffix}");
            var hint = status switch
            {
                400 => "A required NyxID service is not owned by the key's account.",
                403 => "The caller cannot create an API key under the planned owner.",
                _ => "Inspect the NyxID response detail and retry after correcting the owner configuration.",
            };
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_failed", detail, hint, status);
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var id = ReadString(document.RootElement, "id");
            var fullKey = ReadString(document.RootElement, "full_key");
            if (id is null)
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_id");
            if (fullKey is null)
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_full_key");
            return ScheduledAgentApiKeyIssueResult.Succeeded(id, fullKey, keyExpiresAtUnixMs);
        }
        catch (JsonException)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_invalid_json");
        }
    }

    private static bool TryReadErrorEnvelope(
        string? response,
        out int? status,
        out string? body,
        out string? message)
    {
        status = null;
        body = null;
        message = null;
        if (string.IsNullOrWhiteSpace(response))
        {
            message = "empty_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                return false;
            }

            status = root.TryGetProperty("status", out var statusValue) &&
                     statusValue.ValueKind == JsonValueKind.Number &&
                     statusValue.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : null;
            body = ReadString(root, "body");
            message = ReadString(root, "message");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static UserAgentApiKeyRevocationFailureKind ClassifyRevocationFailure(int? status) =>
        status switch
        {
            401 or 403 => UserAgentApiKeyRevocationFailureKind.Unauthorized,
            429 or >= 500 => UserAgentApiKeyRevocationFailureKind.Transient,
            _ => UserAgentApiKeyRevocationFailureKind.ProviderError,
        };

    private static bool TryResolveOwnerScope(
        AuthorizationOwnerIdentity? owner,
        out AuthorizationOwnerKind ownerKind,
        out string ownerSubject)
    {
        ownerKind = AuthorizationOwnerKind.Unspecified;
        ownerSubject = string.Empty;
        if (owner == null ||
            !string.Equals(owner.Authority?.Trim(), NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) ||
            Normalize(owner.OwnerSubject) is not { } normalizedOwnerSubject ||
            owner.OwnerKind is not (AuthorizationOwnerKind.Personal or AuthorizationOwnerKind.Organization))
        {
            return false;
        }

        ownerKind = owner.OwnerKind;
        ownerSubject = normalizedOwnerSubject;
        return true;
    }

    private static bool TryReadMatchingActiveApiKeyIds(
        string response,
        string exactCredentialName,
        out IReadOnlyList<string> apiKeyIds)
    {
        apiKeyIds = [];
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var matching = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object ||
                    !key.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String ||
                    Normalize(idValue.GetString()) is not { } id ||
                    !string.Equals(id, idValue.GetString(), StringComparison.Ordinal) ||
                    !seenIds.Add(id) ||
                    !key.TryGetProperty("name", out var nameValue) ||
                    nameValue.ValueKind != JsonValueKind.String ||
                    nameValue.GetString() is not { Length: > 0 } name ||
                    !key.TryGetProperty("is_active", out var activeValue) ||
                    activeValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                if (activeValue.GetBoolean() &&
                    string.Equals(name, exactCredentialName, StringComparison.Ordinal))
                {
                    matching.Add(id);
                }
            }

            apiKeyIds = matching;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolveAuthorizationPlanMismatch(
        NyxIdApiKeyScopePlan scopePlan,
        ScheduledInvocationAuthorizationPlan plan,
        AuthorizationOwnerKind ownerKind,
        string ownerSubject,
        IReadOnlyList<string> serviceIds,
        IReadOnlyList<string> nodeIds,
        out ScheduledAuthorizationPlanMismatchReason mismatchReason)
    {
        if (!string.Equals(scopePlan.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.ScopePlanAuthorityMismatch;
            return false;
        }

        if (!MatchesScopePlanVersions(scopePlan, plan.CatalogAuthority))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.ScopePlanVersionsMismatch;
            return false;
        }

        if (!MatchesPrincipal(scopePlan.IntendedKeyOwner, ownerKind, ownerSubject))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.IntendedKeyOwnerMismatch;
            return false;
        }

        if (!MatchesPrincipal(scopePlan.AuthenticatedActor, plan.AuthenticatedActor))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.AuthenticatedActorMismatch;
            return false;
        }

        if (scopePlan.Freshness.Mode != NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot ||
            !string.Equals(scopePlan.Freshness.PreconditionField, "scope_plan_digest", StringComparison.Ordinal) ||
            scopePlan.Freshness.PostCreationDrift != NyxIdScopePlanPostCreationDrift.FailClosed)
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.ScopePlanFreshnessMismatch;
            return false;
        }

        if (!scopePlan.Completeness.ListComplete ||
            !scopePlan.Completeness.NoDuplicates ||
            scopePlan.Completeness.RouteCandidateBasis != NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes ||
            !scopePlan.Completeness.TransientNodeStateExcluded)
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.ScopePlanCompletenessMismatch;
            return false;
        }

        if (!scopePlan.AllowedServiceIds.SequenceEqual(serviceIds, StringComparer.Ordinal))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.AllowedServiceIdsMismatch;
            return false;
        }

        if (!scopePlan.AllowedNodeIds.SequenceEqual(nodeIds, StringComparer.Ordinal))
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch;
            return false;
        }

        if (scopePlan.Services.Count != plan.NyxIdServiceGrants.Count)
        {
            mismatchReason = ScheduledAuthorizationPlanMismatchReason.ServiceGrantCountMismatch;
            return false;
        }

        for (var index = 0; index < plan.NyxIdServiceGrants.Count; index++)
        {
            var plannedGrant = plan.NyxIdServiceGrants[index];
            var currentGrant = scopePlan.Services[index];
            if (!string.Equals(currentGrant.UserServiceId, plannedGrant.UserServiceId, StringComparison.Ordinal))
            {
                mismatchReason = ScheduledAuthorizationPlanMismatchReason.ServiceGrantIdentityMismatch;
                return false;
            }

            if (!MatchesPrincipal(currentGrant.ResourceOwner, plannedGrant.ResourceOwner))
            {
                mismatchReason = ScheduledAuthorizationPlanMismatchReason.ServiceGrantResourceOwnerMismatch;
                return false;
            }

            if (!MatchesNodeGrant(currentGrant.NodeGrant, plannedGrant))
            {
                mismatchReason = ScheduledAuthorizationPlanMismatchReason.ServiceGrantNodeMismatch;
                return false;
            }
        }

        mismatchReason = ScheduledAuthorizationPlanMismatchReason.Unspecified;
        return true;
    }

    private static bool MatchesScopePlanVersions(
        NyxIdApiKeyScopePlan scopePlan,
        NyxIdCatalogAuthorityStamp? catalogAuthority) =>
        catalogAuthority is null
            ? string.Equals(
                  scopePlan.ContractVersion,
                  NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                  StringComparison.Ordinal) &&
              string.Equals(
                  scopePlan.PolicyVersion,
                  NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                  StringComparison.Ordinal)
            : string.Equals(scopePlan.ContractVersion, catalogAuthority.ContractVersion, StringComparison.Ordinal) &&
              string.Equals(scopePlan.PolicyVersion, catalogAuthority.PolicyVersion, StringComparison.Ordinal);

    private static bool MatchesPrincipal(
        NyxIdScopePlanPrincipal principal,
        AuthorizationOwnerIdentity? owner) =>
        owner != null &&
        string.Equals(owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        MatchesPrincipal(principal, owner.OwnerKind, owner.OwnerSubject);

    private static bool MatchesPrincipal(
        NyxIdScopePlanPrincipal principal,
        AuthorizationOwnerKind ownerKind,
        string ownerSubject) =>
        principal.Kind == (ownerKind switch
        {
            AuthorizationOwnerKind.Personal => NyxIdScopePlanPrincipalKind.Personal,
            AuthorizationOwnerKind.Organization => NyxIdScopePlanPrincipalKind.Organization,
            _ => NyxIdScopePlanPrincipalKind.Unspecified,
        }) &&
        string.Equals(principal.Id, ownerSubject, StringComparison.Ordinal);

    private static bool MatchesNodeGrant(
        NyxIdScopePlanNodeGrant currentGrant,
        NyxIdServiceGrant plannedGrant) =>
        currentGrant.Kind == (plannedGrant.NodeGrantRequirement switch
        {
            AuthorizationGrantRequirement.NotRequired => NyxIdScopePlanNodeGrantKind.NotRequired,
            AuthorizationGrantRequirement.Required => NyxIdScopePlanNodeGrantKind.Required,
            _ => NyxIdScopePlanNodeGrantKind.Unspecified,
        }) &&
        currentGrant.NodeIds.SequenceEqual(plannedGrant.NodeIds, StringComparer.Ordinal);

    private static bool IsValidServiceGrant(NyxIdServiceGrant grant) =>
        IsNormalized(grant.UserServiceId) &&
        IsNormalized(grant.ServiceSlug) &&
        IsNormalized(grant.DisplayName) &&
        TryResolveOwnerScope(grant.ResourceOwner, out _, out _) &&
        (grant.NodeGrantRequirement is AuthorizationGrantRequirement.NotRequired or
            AuthorizationGrantRequirement.Required) &&
        IsCanonicalIdSequence(grant.NodeIds) &&
        (grant.NodeGrantRequirement == AuthorizationGrantRequirement.Required
            ? grant.NodeIds.Count > 0
            : grant.NodeIds.Count == 0);

    private static bool IsValidAuthenticatedActor(AuthorizationOwnerIdentity? authenticatedActor) =>
        authenticatedActor != null &&
        string.Equals(
            authenticatedActor.Authority,
            NyxIdAuthorizationAuthorities.NyxId,
            StringComparison.Ordinal) &&
        authenticatedActor.OwnerKind == AuthorizationOwnerKind.Personal &&
        IsNormalized(authenticatedActor.OwnerSubject);

    private static bool IsCanonicalIdSequence(IReadOnlyList<string> ids)
    {
        string? previous = null;
        foreach (var id in ids)
        {
            if (!IsNormalized(id) ||
                previous != null && StringComparer.Ordinal.Compare(previous, id) >= 0)
            {
                return false;
            }
            previous = id;
        }
        return true;
    }

    private static bool IsNormalized(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool TryResolveFutureExpiry(
        Google.Protobuf.WellKnownTypes.Timestamp? value,
        DateTimeOffset now,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (value == null)
            return false;
        try
        {
            expiresAt = value.ToDateTimeOffset();
            return expiresAt > now;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ResolveScopes(IEnumerable<NyxIdCredentialScope> scopes)
    {
        var values = new List<string>();
        foreach (var scope in scopes)
        {
            values.Add(scope switch
            {
                NyxIdCredentialScope.Read => "read",
                NyxIdCredentialScope.Proxy => "proxy",
                _ => string.Empty,
            });
        }
        return values.Count > 0 && values.All(static value => value.Length > 0) &&
               values.Distinct(StringComparer.Ordinal).Count() == values.Count
            ? string.Join(' ', values)
            : null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
