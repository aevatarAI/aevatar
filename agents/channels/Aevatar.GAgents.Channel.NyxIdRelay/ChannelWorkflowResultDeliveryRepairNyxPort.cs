using System.Globalization;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal interface IChannelWorkflowResultDeliveryRepairNyxPort
{
    Task<ChannelRotatedNyxAgentCredential> RotateAgentKeyAsync(
        string accessToken,
        string apiKeyId,
        CancellationToken ct);

    Task<IReadOnlyList<ChannelNyxAgentKeySummary>> ListAgentKeysAsync(
        string accessToken,
        CancellationToken ct);

    Task RebindConversationRouteAsync(
        string accessToken,
        string routeId,
        string apiKeyId,
        CancellationToken ct);
}

internal sealed record ChannelNyxAgentKeySummary(
    string ApiKeyId,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);

internal sealed class ChannelRotatedNyxAgentCredential
{
    public ChannelRotatedNyxAgentCredential(
        string apiKeyId,
        string fullKey,
        DateTimeOffset createdAtUtc)
    {
        ApiKeyId = apiKeyId;
        FullKey = fullKey;
        CreatedAtUtc = createdAtUtc;
    }

    public string ApiKeyId { get; }
    public string FullKey { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    public override string ToString() =>
        $"ChannelRotatedNyxAgentCredential {{ ApiKeyId = {ApiKeyId}, FullKey = [REDACTED], CreatedAtUtc = {CreatedAtUtc:O} }}";
}

internal sealed class ChannelWorkflowResultDeliveryRepairNyxPort
    : IChannelWorkflowResultDeliveryRepairNyxPort
{
    private const string FailurePrefix = "channel_workflow_delivery_repair_nyx_";
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelWorkflowResultDeliveryRepairNyxPort> _logger;

    public ChannelWorkflowResultDeliveryRepairNyxPort(
        NyxIdApiClient nyxClient,
        ILogger<ChannelWorkflowResultDeliveryRepairNyxPort> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChannelRotatedNyxAgentCredential> RotateAgentKeyAsync(
        string accessToken,
        string apiKeyId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);

        // Rotation preserves the existing key policy. Historical Lark relay keys were created
        // with only `read write`; prepare the source key with `proxy` first so the rotated Agent
        // Key can authorize every NyxID-backed workflow step, not only terminal reply delivery.
        await ChannelNyxIdAgentKeyScopePolicy.EnsureProxyScopeAsync(
            _nyxClient,
            accessToken,
            apiKeyId,
            _logger,
            ct);

        var response = await _nyxClient.RotateApiKeyAsync(accessToken, apiKeyId.Trim(), ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw Controlled("rotation_failed");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var rotatedId = RequiredString(root, "id", "rotation_missing_id");
            var fullKey = RequiredString(root, "full_key", "rotation_missing_full_key");
            var createdAt = RequiredTimestamp(root, "created_at", "rotation_invalid_created_at");
            _logger.LogInformation(
                "Rotated NyxID relay API key for channel workflow delivery repair: previousKeyId={PreviousKeyId}, rotatedKeyId={RotatedKeyId}",
                apiKeyId.Trim(),
                rotatedId);
            return new ChannelRotatedNyxAgentCredential(rotatedId, fullKey, createdAt);
        }
        catch (JsonException)
        {
            throw Controlled("rotation_invalid_json");
        }
    }

    public async Task<IReadOnlyList<ChannelNyxAgentKeySummary>> ListAgentKeysAsync(
        string accessToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var response = await _nyxClient.ListApiKeysAsync(accessToken, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw Controlled("list_failed");

        try
        {
            using var document = JsonDocument.Parse(response);
            var items = ResolveArray(document.RootElement);
            var result = new List<ChannelNyxAgentKeySummary>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw Controlled("list_invalid_item");

                var id = RequiredString(item, "id", "list_missing_id");
                var name = RequiredString(item, "name", "list_missing_name");
                if (!item.TryGetProperty("is_active", out var active) ||
                    active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Controlled("list_invalid_active_state");
                }

                result.Add(new ChannelNyxAgentKeySummary(
                    id,
                    name,
                    active.GetBoolean(),
                    RequiredTimestamp(item, "created_at", "list_invalid_created_at")));
            }

            return result;
        }
        catch (JsonException)
        {
            throw Controlled("list_invalid_json");
        }
    }

    public async Task RebindConversationRouteAsync(
        string accessToken,
        string routeId,
        string apiKeyId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);

        var response = await _nyxClient.UpdateConversationRouteAsync(
            accessToken,
            routeId.Trim(),
            JsonSerializer.Serialize(new
            {
                agent_api_key_id = apiKeyId.Trim(),
                default_agent = true,
            }),
            ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw Controlled("route_update_failed");

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Controlled("route_update_invalid_response");
        }
        catch (JsonException)
        {
            throw Controlled("route_update_invalid_json");
        }

        _logger.LogInformation(
            "Rebound existing NyxID conversation route for channel workflow delivery repair: routeId={RouteId}, apiKeyId={ApiKeyId}",
            routeId.Trim(),
            apiKeyId.Trim());
    }

    internal static string RelayKeyName(string registrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
        var normalized = registrationId.Trim();
        return $"aevatar-lark-relay-{normalized[..Math.Min(12, normalized.Length)]}";
    }

    private static JsonElement ResolveArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        if (root.ValueKind != JsonValueKind.Object)
            throw Controlled("list_invalid_shape");

        foreach (var propertyName in new[] { "items", "data", "api_keys", "keys" })
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        throw Controlled("list_invalid_shape");
    }

    private static string RequiredString(JsonElement root, string propertyName, string failure)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw Controlled(failure);
        var normalized = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? throw Controlled(failure) : normalized;
    }

    private static DateTimeOffset RequiredTimestamp(
        JsonElement root,
        string propertyName,
        string failure)
    {
        var value = RequiredString(root, propertyName, failure);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : throw Controlled(failure);
    }

    private static InvalidOperationException Controlled(string failure) =>
        new(FailurePrefix + failure);
}
