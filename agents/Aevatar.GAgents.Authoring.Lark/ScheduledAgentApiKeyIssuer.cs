using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Authorization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

internal sealed class ScheduledAgentApiKeyIssuer : IScheduledAgentApiKeyIssuer
{
    private static readonly JsonSerializerOptions CreateKeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly ILogger<ScheduledAgentApiKeyIssuer>? _logger;

    public ScheduledAgentApiKeyIssuer(
        INyxIdApiClientFactory nyxClientFactory,
        ScheduledAgentCreatorOptions options,
        ILogger<ScheduledAgentApiKeyIssuer>? logger = null)
    {
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
    }

    public async Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ScheduledInvocationAuthorizationPlan plan,
        string credentialName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);

        var serviceIds = plan.NyxIdServiceGrants
            .Select(static grant => grant.UserServiceId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var nodeIds = plan.NyxIdServiceGrants
            .SelectMany(static grant => grant.NodeGrants)
            .Select(static grant => grant.NodeId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (serviceIds.Length == 0 && !plan.CredentialPolicy.ServiceGrantsNotRequired ||
            plan.NyxIdServiceGrants.Any(static grant => !grant.NodeGrantsNotRequired) && nodeIds.Length == 0)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("authorization_plan_grants_invalid");
        }

        var response = await _nyxClientFactory.CreateClient().CreateApiKeyAsync(
            token,
            JsonSerializer.Serialize(new
            {
                name = credentialName.Trim(),
                scopes = plan.CredentialPolicy.Scopes,
                platform = "generic",
                allow_all_services = plan.CredentialPolicy.AllowAllServices,
                allow_all_nodes = plan.CredentialPolicy.AllowAllNodes,
                allowed_service_ids = serviceIds,
                allowed_node_ids = nodeIds,
                target_org_id = plan.Owner.OwnerKind == NyxIdCatalogOwnerKind.Organization
                    ? plan.Owner.OwnerSubject
                    : null,
                expires_at = plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset().ToString("O"),
            }, CreateKeyJsonOptions),
            ct);

        return ExtractIssuedKey(response, plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds());
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

    public async Task TryRevokeAsync(string token, string apiKeyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(apiKeyId))
            return;

        try
        {
            var result = await RevokeAsync(token, apiKeyId, ct);
            if (!result.Completed)
            {
                _logger?.LogWarning(
                    "Scheduled agent API key rollback remains pending: apiKeyId={ApiKeyId} status={Status} failureKind={FailureKind} error={Error}",
                    apiKeyId,
                    result.HttpStatus,
                    result.FailureKind,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scheduled agent API key rollback failed: apiKeyId={ApiKeyId}", apiKeyId);
        }
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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
