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
            owner == null ||
            !string.Equals(owner.Authority?.Trim(), NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) ||
            owner.OwnerKind == AuthorizationOwnerKind.Unspecified ||
            !Enum.IsDefined(owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(owner.OwnerSubject) ||
            !TryResolveFutureExpiry(policy.ExpiresAt, _timeProvider.GetUtcNow(), out var expiresAt))
        {
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_policy_invalid");
        }

        var serviceIds = plan.NyxIdServiceGrants
            .Select(static grant => grant.UserServiceId)
            .ToArray();
        var nodeIds = plan.NyxIdNodeGrants
            .Select(static grant => grant.NodeId)
            .ToArray();
        if (string.IsNullOrWhiteSpace(plan.PermissionDigest) ||
            !IsExactIdSequence(serviceIds) ||
            !IsExactIdSequence(nodeIds) ||
            plan.NyxIdNodeGrants.Any(grant =>
                !serviceIds.Contains(grant.UserServiceId, StringComparer.Ordinal) ||
                grant.Role == NyxIdNodeRole.Unspecified ||
                !Enum.IsDefined(grant.Role) ||
                grant.EdgeKind == NyxIdNodeEdgeKind.Unspecified ||
                !Enum.IsDefined(grant.EdgeKind) ||
                grant.EdgeKind == NyxIdNodeEdgeKind.NodeBinding &&
                string.IsNullOrWhiteSpace(grant.BindingId) ||
                grant.EdgeKind == NyxIdNodeEdgeKind.UserServicePrimary &&
                (!string.IsNullOrWhiteSpace(grant.BindingId) || grant.Role != NyxIdNodeRole.Primary)) ||
            serviceIds.Length == 0 && policy.ServiceGrantRequirement == AuthorizationGrantRequirement.Required ||
            nodeIds.Length == 0 && policy.NodeGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_grants_invalid");
        }
        var scopes = ResolveScopes(policy.Scopes);
        if (scopes == null)
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_scopes_invalid");

        var response = await _nyxClientFactory.CreateClient().CreateApiKeyAsync(
            token,
            JsonSerializer.Serialize(new
            {
                name = credentialName.Trim(),
                scopes,
                platform = "generic",
                allow_all_services = false,
                allow_all_nodes = false,
                allowed_service_ids = serviceIds,
                allowed_node_ids = nodeIds,
                target_org_id = owner.OwnerKind == AuthorizationOwnerKind.Organization
                    ? owner.OwnerSubject.Trim()
                    : null,
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

    private static bool IsExactIdSequence(IReadOnlyList<string> ids) =>
        ids.All(static id =>
            !string.IsNullOrWhiteSpace(id) &&
            string.Equals(id, id.Trim(), StringComparison.Ordinal));

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
