using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>Optional deployment-owned metadata fallbacks for /v1/models entries whose upstream
/// `/v1/models` response was sparse (no context_length / max_output_tokens / display_name).
/// Lookup precedence per entry: upstream-forwarded fields → `Aevatar:Responses:ModelMetadataFallbacks["slug/model"]`
/// (exact match) → `…["slug"]` (group-wide). The fallback only fills NULL fields; never overwrites
/// upstream-provided values. Empty config = no fallback (default for new deployments).
/// Lives in config rather than code because the values are deployment-specific
/// (different NyxID instances expose different backing LLMs) and `feedback_no_hardcoded_metadata`
/// explicitly forbids slug→metadata tables inside aevatar source.</summary>
internal sealed class ResponsesModelMetadataFallbackOptions
{
    public const string SectionName = "Aevatar:Responses:ModelMetadataFallbacks";

    public Dictionary<string, ResponsesModelMetadataFallback> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ResponsesModelMetadataFallback
{
    public int? ContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}

/// <summary>Aggregates concrete model strings across every NyxID-routed service the caller can reach.
/// NyxID itself does not expose a global concrete-model catalog (`/llm/status` only carries
/// wildcard patterns like `gpt-*`); per-provider models are obtained by fan-out to each ready
/// service's `/v1/models` plane. See reference_nyxid_model_surface_matrix and
/// reference_aevatar_llm_route_aggregation for the data sources.</summary>
internal interface IResponsesModelsAggregator
{
    Task<IReadOnlyList<ResponsesModelEntry>> AggregateAsync(string bearerToken, CancellationToken ct);
}

internal sealed class NyxIdResponsesModelsAggregator : IResponsesModelsAggregator
{
    private readonly IUserLlmCatalogPort _catalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NyxIdResponsesModelsAggregator> _logger;
    private readonly ResponsesModelMetadataFallbackOptions _fallbacks;

    public NyxIdResponsesModelsAggregator(
        IUserLlmCatalogPort catalog,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdResponsesModelsAggregator> logger,
        IOptions<ResponsesModelMetadataFallbackOptions>? fallbackOptions = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallbacks = fallbackOptions?.Value ?? new ResponsesModelMetadataFallbackOptions();
    }

    public async Task<IReadOnlyList<ResponsesModelEntry>> AggregateAsync(
        string bearerToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var authority = ResolveAuthorityBase();
        if (string.IsNullOrWhiteSpace(authority))
        {
            _logger.LogWarning("NyxID authority is not configured; returning empty models list.");
            return Array.Empty<ResponsesModelEntry>();
        }

        NyxIdLlmServicesResult catalog;
        try
        {
            catalog = await _catalog.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load NyxID LLM services catalog; returning empty models list.");
            return Array.Empty<ResponsesModelEntry>();
        }

        var readyServices = catalog.Services
            .Where(IsReadyForFanOut)
            .ToList();
        if (readyServices.Count == 0)
            return Array.Empty<ResponsesModelEntry>();

        var fetchTasks = readyServices
            .Select(service => FetchAndNormalizeAsync(authority, service, bearerToken, ct))
            .ToList();
        var groups = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        var entries = groups.SelectMany(static g => g).ToList();
        return ApplyMetadataFallbacks(entries, _fallbacks.Entries);
    }

    /// <summary>Post-fan-out merge: for each entry, if the upstream-forwarded metadata fields are
    /// null, look up a fallback first by `{slug}/{model}` (exact) then by `{slug}` (group-wide).
    /// Fallback only fills nulls — never overwrites upstream values. Empty fallback dict is a no-op.</summary>
    internal static IReadOnlyList<ResponsesModelEntry> ApplyMetadataFallbacks(
        IReadOnlyList<ResponsesModelEntry> entries,
        IReadOnlyDictionary<string, ResponsesModelMetadataFallback> fallbacks)
    {
        if (fallbacks.Count == 0)
            return entries;

        var result = new List<ResponsesModelEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var fb = ResolveFallback(entry, fallbacks);
            if (fb is null)
            {
                result.Add(entry);
                continue;
            }

            result.Add(entry with
            {
                ContextLength = entry.ContextLength ?? fb.ContextLength,
                MaxOutputTokens = entry.MaxOutputTokens ?? fb.MaxOutputTokens,
                DisplayName = entry.DisplayName ?? fb.DisplayName,
                Description = entry.Description ?? fb.Description,
            });
        }
        return result;
    }

    private static ResponsesModelMetadataFallback? ResolveFallback(
        ResponsesModelEntry entry,
        IReadOnlyDictionary<string, ResponsesModelMetadataFallback> fallbacks)
    {
        if (fallbacks.TryGetValue(entry.Id, out var specific))
            return specific;
        if (!string.IsNullOrWhiteSpace(entry.Group) && fallbacks.TryGetValue(entry.Group, out var groupLevel))
            return groupLevel;
        return null;
    }

    private async Task<IReadOnlyList<ResponsesModelEntry>> FetchAndNormalizeAsync(
        string authority,
        NyxIdLlmService service,
        string bearerToken,
        CancellationToken ct)
    {
        var url = BuildModelsUrl(authority, service.RouteValue);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Skipping models for service {Slug}: HTTP {Status} from {Url}",
                    service.ServiceSlug,
                    (int)response.StatusCode,
                    url);
                return Array.Empty<ResponsesModelEntry>();
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return NormalizeModelsBody(body, service);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Models fetch failed for service {Slug} at {Url}; continuing without it.",
                service.ServiceSlug,
                url);
            return Array.Empty<ResponsesModelEntry>();
        }
    }

    /// <summary>Fan-out gate: we want every catalog entry with a route to be tried, because
    /// `Allowed` / `Status` from <c>NyxIdLlmServiceCatalogParser</c> mostly reflect "user has bound
    /// their credential" — but several catalog entries (e.g. chrono-llm, which has
    /// <c>requires_connection=true</c> + <c>connected=false</c> in /proxy/services) are still
    /// functional for proxy traffic because the backing LLM is shared at the deployment level. The
    /// honest behavior is to ATTEMPT the fan-out and let the downstream's HTTP response (200, 403,
    /// 404) decide whether the service surfaces models; a UI-level "you haven't connected this"
    /// hint should not be conflated with "this route can't serve `/v1/models`".</summary>
    private static bool IsReadyForFanOut(NyxIdLlmService service) =>
        !string.IsNullOrWhiteSpace(service.RouteValue);

    /// <summary>Both NyxID route planes already terminate at an OpenAI-compatible API root: the
    /// GatewayProvider plane (`/api/v1/llm/&lt;slug&gt;/v1`) is the API root verbatim, and the
    /// ProxyService / UserService plane (`/api/v1/proxy/s/&lt;slug&gt;`) routes to the
    /// DownstreamService's stored <c>base_url</c> which itself already includes the upstream's
    /// `/v1` segment (e.g. chrono-llm.base_url = <c>https://llm.aelf.dev/v1</c>, so
    /// `/proxy/s/chrono-llm/models` lands at <c>https://llm.aelf.dev/v1/models</c>). Append
    /// `/models` for every route — appending `/v1/models` would double the segment on the proxy
    /// plane and 404.</summary>
    internal static string BuildModelsUrl(string authority, string routeValue) =>
        $"{authority.TrimEnd('/')}{routeValue.TrimEnd('/')}/models";

    internal static IReadOnlyList<ResponsesModelEntry> NormalizeModelsBody(
        string body,
        NyxIdLlmService service)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<ResponsesModelEntry>();

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<ResponsesModelEntry>();
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ResponsesModelEntry>();
            }

            // OpenRouter-style: prefix EVERY model id with the service slug
            // (e.g. `anthropic/claude-opus-4-7`, `chrono-llm/gpt-5.5`). Stage 2's
            // ResponsesRouteResolver consults the catalog to map a recovered
            // slug back to its RouteValue, so prefixed gateway entries route
            // through `/api/v1/llm/<slug>/v1` rather than the unrelated
            // `/proxy/s/<slug>`. Bare model strings (no slash) keep working as
            // a back-compat path through the default gateway.
            var createdFallback = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var entries = new List<ResponsesModelEntry>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idProp) ||
                    idProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelId = idProp.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                entries.Add(new ResponsesModelEntry
                {
                    Id = $"{service.ServiceSlug}/{modelId}",
                    Created = ReadCreated(item) ?? createdFallback,
                    OwnedBy = service.ServiceSlug,
                    Group = service.ServiceSlug,
                    RouteValue = service.RouteValue,
                    Status = service.Status,
                    // Forward upstream metadata when present. Field-name aliases cover the
                    // common shapes — OpenRouter uses `context_length` / `max_output_tokens`,
                    // anthropic uses `max_input_tokens` / `max_tokens` (output), some
                    // OpenAI-derived backends use `max_completion_tokens`. Null when the
                    // upstream entry was minimal — caller-side clients fall back to safe
                    // defaults and may emit a UI warning, but the route still works.
                    ContextLength = ReadInt32(item, "context_length", "max_input_tokens"),
                    MaxOutputTokens = ReadInt32(item, "max_output_tokens", "max_completion_tokens", "max_tokens"),
                    DisplayName = ReadString(item, "display_name", "name"),
                    Description = ReadString(item, "description", "summary"),
                });
            }

            return entries;
        }
        catch (JsonException)
        {
            return Array.Empty<ResponsesModelEntry>();
        }
    }

    private static long? ReadCreated(JsonElement element)
    {
        if (element.TryGetProperty("created", out var createdProp) &&
            createdProp.ValueKind == JsonValueKind.Number &&
            createdProp.TryGetInt64(out var unix))
        {
            return unix;
        }

        if (element.TryGetProperty("created_at", out var createdAt) &&
            createdAt.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(createdAt.GetString(), out var dto))
        {
            return dto.ToUnixTimeSeconds();
        }

        return null;
    }

    private static int? ReadInt32(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        return null;
    }

    /// <summary>Mirrors the authority-resolution chain in
    /// <c>NyxIdLlmCatalogHttpClient.ResolveNyxIdAuthorityBase</c> so both clients agree on which
    /// NyxID instance to fan out against.</summary>
    private string? ResolveAuthorityBase()
    {
        var authority = _configuration["Cli:App:NyxId:Authority"]
            ?? _configuration["Aevatar:NyxId:Authority"]
            ?? _configuration["Aevatar:Authentication:Authority"];

        if (string.IsNullOrWhiteSpace(authority))
            return null;

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        const string gatewaySuffix = "/api/v1/llm/gateway/v1";
        return trimmed.EndsWith(gatewaySuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^gatewaySuffix.Length]
            : trimmed;
    }
}
