using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

internal sealed class ScheduledAgentApiKeyIssuer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly ScheduledAgentCreatorOptions _options;
    private readonly ILogger<ScheduledAgentApiKeyIssuer>? _logger;

    public ScheduledAgentApiKeyIssuer(
        INyxIdApiClientFactory nyxClientFactory,
        ScheduledAgentCreatorOptions options,
        ILogger<ScheduledAgentApiKeyIssuer>? logger = null)
    {
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ScheduledAgentServiceSlugs serviceSlugs,
        string agentId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(serviceSlugs);

        var slugs = RequiredSlugs(serviceSlugs).Distinct(StringComparer.Ordinal).ToArray();
        if (slugs.Length == 0)
            return ScheduledAgentApiKeyIssueResult.Failed("missing_required_service_slugs");

        var client = _nyxClientFactory.CreateClient();
        var servicesJson = await client.ListServicesAsync(token, ct);
        var resolution = ResolveServiceIds(servicesJson, slugs);
        if (resolution.Error is not null)
            return ScheduledAgentApiKeyIssueResult.Failed(resolution.Error);

        var response = await client.CreateApiKeyAsync(
            token,
            JsonSerializer.Serialize(new
            {
                name = $"aevatar-scheduled-agent-{agentId}",
                scopes = "read write",
                platform = "generic",
                allow_all_services = false,
                allow_all_nodes = true,
                allowed_service_ids = resolution.ServiceIds,
            }, JsonOptions),
            ct);

        return ExtractIssuedKey(response);
    }

    public async Task TryRevokeAsync(string token, string apiKeyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(apiKeyId))
            return;

        try
        {
            var response = await _nyxClientFactory.CreateClient().DeleteApiKeyAsync(token, apiKeyId.Trim(), ct);
            if (LooksLikeErrorEnvelope(response))
            {
                _logger?.LogWarning(
                    "Scheduled agent API key rollback returned an error envelope: apiKeyId={ApiKeyId}, response={Response}",
                    apiKeyId,
                    response);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scheduled agent API key rollback failed: apiKeyId={ApiKeyId}", apiKeyId);
        }
    }

    private IEnumerable<string> RequiredSlugs(ScheduledAgentServiceSlugs serviceSlugs)
    {
        var ornnSlug = Normalize(_options.OrnnServiceSlug) ?? ScheduledAgentCreatorOptions.DefaultOrnnServiceSlug;
        yield return ornnSlug;
        yield return serviceSlugs.PrimaryOutboundSlug;

        if (!string.IsNullOrWhiteSpace(serviceSlugs.FailureNotificationSlug) &&
            !string.Equals(serviceSlugs.FailureNotificationSlug, serviceSlugs.PrimaryOutboundSlug, StringComparison.Ordinal))
        {
            yield return serviceSlugs.FailureNotificationSlug;
        }
    }

    private static ScheduledAgentServiceResolution ResolveServiceIds(string json, IReadOnlyCollection<string> requiredSlugs)
    {
        if (LooksLikeErrorEnvelope(json))
            return ScheduledAgentServiceResolution.Failed("service_resolution_failed");

        var matches = requiredSlugs
            .Select(static slug => (slug, ids: new List<string>()))
            .ToDictionary(static x => x.slug, static x => x.ids, StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in EnumerateServiceItems(document.RootElement))
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var slug = ReadString(item, "slug") ?? ReadString(item, "service_slug");
                if (slug is null || !matches.TryGetValue(slug, out var ids))
                    continue;

                var id = ReadString(item, "id") ?? ReadString(item, "service_id") ?? ReadString(item, "user_service_id");
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id.Trim());
            }
        }
        catch (JsonException)
        {
            return ScheduledAgentServiceResolution.Failed("service_resolution_invalid_json");
        }

        var resolved = new List<string>();
        foreach (var slug in requiredSlugs.Distinct(StringComparer.Ordinal))
        {
            var ids = matches[slug].Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0)
                return ScheduledAgentServiceResolution.Failed($"required_service_not_found:{slug}");
            if (ids.Length > 1)
                return ScheduledAgentServiceResolution.Failed($"required_service_ambiguous:{slug}");

            resolved.Add(ids[0]);
        }

        return resolved.Count == 0
            ? ScheduledAgentServiceResolution.Failed("required_service_ids_empty")
            : new ScheduledAgentServiceResolution(resolved, null);
    }

    private static ScheduledAgentApiKeyIssueResult ExtractIssuedKey(string response)
    {
        if (LooksLikeErrorEnvelope(response))
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_failed");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var id = ReadString(root, "id");
            var fullKey = ReadString(root, "full_key");
            if (string.IsNullOrWhiteSpace(id))
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_id");
            if (string.IsNullOrWhiteSpace(fullKey))
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_full_key");

            return ScheduledAgentApiKeyIssueResult.Succeeded(id.Trim(), fullKey.Trim());
        }
        catch (JsonException)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_invalid_json");
        }
    }

    private static IEnumerable<JsonElement> EnumerateServiceItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "user_services", "keys", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    private static bool LooksLikeErrorEnvelope(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private sealed record ScheduledAgentServiceResolution(IReadOnlyList<string> ServiceIds, string? Error)
    {
        public static ScheduledAgentServiceResolution Failed(string error) => new([], error);
    }
}

internal sealed record ScheduledAgentServiceSlugs(
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug);

internal sealed record ScheduledAgentApiKeyIssueResult(
    bool Success,
    string? ApiKeyId,
    string? FullKey,
    string? Error)
{
    public static ScheduledAgentApiKeyIssueResult Succeeded(string apiKeyId, string fullKey) =>
        new(true, apiKeyId, fullKey, null);

    public static ScheduledAgentApiKeyIssueResult Failed(string error) =>
        new(false, null, null, error);
}
