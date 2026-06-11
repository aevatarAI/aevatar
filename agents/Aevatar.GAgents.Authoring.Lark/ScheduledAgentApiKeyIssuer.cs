using System.Text.Json;
using System.Text.Json.Serialization;
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
        string skillName,
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
                scopes = "read write proxy",
                platform = "generic",
                allow_all_services = false,
                allow_all_nodes = true,
                allowed_service_ids = resolution.ServiceIds,
            }, JsonOptions),
            ct);

        var issuedKey = ExtractIssuedKey(response);
        if (!issuedKey.Success)
            return issuedKey;

        if (string.IsNullOrWhiteSpace(skillName))
            return issuedKey;

        var ornnSlug = GetOrnnServiceSlug();
        var preflight = await PreflightSkillFetchAsync(client, issuedKey.FullKey!, ornnSlug, skillName, ct);
        if (preflight.Success)
            return issuedKey;

        await TryRevokeAsync(token, issuedKey.ApiKeyId ?? string.Empty, CancellationToken.None);
        return ScheduledAgentApiKeyIssueResult.Failed(
            preflight.Error ?? "scheduled_skill_preflight_failed",
            preflight.Detail,
            preflight.Hint,
            preflight.HttpStatus,
            ornnSlug,
            skillName.Trim());
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
        var ornnSlug = GetOrnnServiceSlug();
        if (serviceSlugs.RequiresOrnnService)
            yield return ornnSlug;
        yield return serviceSlugs.PrimaryOutboundSlug;

        if (!string.IsNullOrWhiteSpace(serviceSlugs.FailureNotificationSlug) &&
            !string.Equals(serviceSlugs.FailureNotificationSlug, serviceSlugs.PrimaryOutboundSlug, StringComparison.Ordinal))
        {
            yield return serviceSlugs.FailureNotificationSlug;
        }

        foreach (var slug in serviceSlugs.RequiredServiceSlugs)
        {
            if (!string.IsNullOrWhiteSpace(slug))
                yield return slug;
        }
    }

    private async Task<ScheduledAgentSkillPreflightResult> PreflightSkillFetchAsync(
        NyxIdApiClient client,
        string apiKey,
        string ornnSlug,
        string skillName,
        CancellationToken ct)
    {
        var normalizedSkillName = skillName.Trim();
        var response = await client.ProxyRequestAsync(
            apiKey,
            ornnSlug,
            $"/api/v1/skills/{Uri.EscapeDataString(normalizedSkillName)}/json",
            "GET",
            null,
            null,
            ct);

        if (!TryReadErrorEnvelope(response, out var status, out var body, out var message))
            return ScheduledAgentSkillPreflightResult.Succeeded();

        var detailSuffix = string.IsNullOrWhiteSpace(body)
            ? message
            : body;
        var detail = status switch
        {
            403 => $"NyxID proxy returned 403 while fetching scheduled skill '{normalizedSkillName}' with the newly issued agent key. " +
                   $"The key is missing proxy scope or service authorization for service '{ornnSlug}'." +
                   (string.IsNullOrWhiteSpace(detailSuffix) ? string.Empty : $" Response: {detailSuffix}"),
            404 => $"NyxID proxy returned 404 while fetching scheduled skill '{normalizedSkillName}' through service '{ornnSlug}'." +
                   (string.IsNullOrWhiteSpace(detailSuffix) ? string.Empty : $" Response: {detailSuffix}"),
            _ => $"NyxID proxy preflight failed while fetching scheduled skill '{normalizedSkillName}' through service '{ornnSlug}'" +
                 (status.HasValue ? $" with status {status.Value}" : string.Empty) +
                 (string.IsNullOrWhiteSpace(detailSuffix) ? "." : $". Response: {detailSuffix}"),
        };

        return status switch
        {
            403 => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_access_denied",
                detail,
                "Connect the Ornn service in NyxID and recreate the scheduled agent so its scoped key includes proxy access to that UserService.",
                status),
            404 => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_skill_not_found",
                detail,
                "Check skill_ref and ensure the referenced Ornn skill is visible to the scheduled agent credential.",
                status),
            _ => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_failed",
                detail,
                "Fix the NyxID Ornn service binding or skill reference, then retry scheduled agent creation.",
                status),
        };
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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private string GetOrnnServiceSlug() =>
        Normalize(_options.OrnnServiceSlug) ?? ScheduledAgentCreatorOptions.DefaultOrnnServiceSlug;

    private sealed record ScheduledAgentServiceResolution(IReadOnlyList<string> ServiceIds, string? Error)
    {
        public static ScheduledAgentServiceResolution Failed(string error) => new([], error);
    }
}

internal sealed record ScheduledAgentSkillPreflightResult(
    bool Success,
    string? Error,
    string? Detail,
    string? Hint,
    int? HttpStatus)
{
    public static ScheduledAgentSkillPreflightResult Succeeded() =>
        new(true, null, null, null, null);

    public static ScheduledAgentSkillPreflightResult Failed(
        string error,
        string detail,
        string hint,
        int? httpStatus) =>
        new(false, error, detail, hint, httpStatus);
}

internal sealed record ScheduledAgentServiceSlugs(
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug,
    IReadOnlyList<string> RequiredServiceSlugs,
    bool RequiresOrnnService = true);

internal sealed record ScheduledAgentApiKeyIssueResult(
    bool Success,
    string? ApiKeyId,
    string? FullKey,
    string? Error,
    string? Detail = null,
    string? Hint = null,
    int? HttpStatus = null,
    string? ServiceSlug = null,
    string? SkillRef = null)
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ScheduledAgentApiKeyIssueResult Succeeded(string apiKeyId, string fullKey) =>
        new(true, apiKeyId, fullKey, null);

    public static ScheduledAgentApiKeyIssueResult Failed(
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null) =>
        new(false, null, null, error, detail, hint, httpStatus, serviceSlug, skillRef);

    public string ToErrorJson() =>
        JsonSerializer.Serialize(new
        {
            error = Error ?? "api_key_issue_failed",
            detail = Detail,
            hint = Hint,
            http_status = HttpStatus,
            service_slug = ServiceSlug,
            skill_ref = SkillRef,
        }, ErrorJsonOptions);
}
