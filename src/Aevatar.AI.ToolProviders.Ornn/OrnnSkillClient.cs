using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Ornn skill API client. Routes through NyxID's proxy so the Ornn upstream URL stays a
/// runtime concern (resolved by NyxID from the user's bound <c>ornn-api</c> service) rather
/// than a hardcoded constant. The public Ornn frontend URL only serves the SPA shell, so
/// direct calls return HTML for any path — the NyxID-routed path is the canonical surface
/// (issue #530 follow-up).
/// </summary>
public sealed class OrnnSkillClient
{
    private readonly NyxIdApiClient _nyxApi;
    private readonly OrnnOptions _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OrnnSkillClient(OrnnOptions options, NyxIdApiClient nyxApi, ILogger<OrnnSkillClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxApi = nyxApi ?? throw new ArgumentNullException(nameof(nyxApi));
        _logger = logger ?? NullLogger<OrnnSkillClient>.Instance;
    }

    /// <summary>搜索技能。</summary>
    public async Task<OrnnSearchResult> SearchSkillsAsync(
        string accessToken,
        string query = "",
        string scope = "mixed",
        int page = 1,
        int pageSize = 20,
        string mode = "keyword",
        CancellationToken ct = default)
    {
        var normalizedMode = string.Equals(mode, "semantic", StringComparison.OrdinalIgnoreCase)
            ? "semantic"
            : "keyword";
        var normalizedScope = scope.ToLowerInvariant() is "public" or "private" or "mixed"
            ? scope.ToLowerInvariant()
            : "mixed";
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var path = $"/api/web/skill-search?query={Uri.EscapeDataString(query)}&mode={normalizedMode}&scope={Uri.EscapeDataString(normalizedScope)}&page={page}&pageSize={pageSize}";

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: ct);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSearchResult { Items = [], Error = proxyError };

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSearchResult>>(response, JsonOptions);
            return envelope?.Data ?? new OrnnSearchResult { Items = [] };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn skill search failed for query '{Query}'", query);
            return new OrnnSearchResult { Items = [], Error = ex.Message };
        }
    }

    /// <summary>获取技能 JSON（含文件内容）。</summary>
    public async Task<OrnnSkillJson?> GetSkillJsonAsync(
        string accessToken,
        string idOrName,
        CancellationToken ct = default)
    {
        var path = $"/api/web/skills/{Uri.EscapeDataString(idOrName)}/json";

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: ct);

            if (TryUnwrapNyxIdProxyError(response, out _))
                return null;

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSkillJson>>(response, JsonOptions);
            return envelope?.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn get skill failed for '{IdOrName}'", idOrName);
            return null;
        }
    }

    /// <summary>
    /// Detect the wrapped error envelope NyxIdApiClient.SendAsync emits when the upstream
    /// returns non-2xx (<c>{"error": true, "status": N, "body": "..."}</c>) so callers see a
    /// concise message instead of a JsonException about the wrapper shape.
    /// </summary>
    private static bool TryUnwrapNyxIdProxyError(string response, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = root.TryGetProperty("status", out var statusProp) &&
                         statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32().ToString()
                : "unknown";
            detail = $"NyxID proxy error (status={status})";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// ─── DTOs ───

public sealed class OrnnApiResponse<T>
{
    public T? Data { get; set; }
    public string? Error { get; set; }
}

public sealed class OrnnSearchResult
{
    public string? SearchMode { get; set; }
    public string? SearchScope { get; set; }
    public int Total { get; set; }
    public int TotalPages { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<OrnnSkillSummary> Items { get; set; } = [];
    [JsonIgnore] public string? Error { get; set; }
}

public sealed class OrnnSkillSummary
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsPrivate { get; set; }
    public List<string>? Tags { get; set; }
    public OrnnSkillMetadata? Metadata { get; set; }
}

public sealed class OrnnSkillMetadata
{
    public string? Category { get; set; }
    [JsonPropertyName("tag")]
    public List<string>? Tags { get; set; }
}

public sealed class OrnnSkillJson
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public OrnnSkillMetadata? Metadata { get; set; }
    public Dictionary<string, string>? Files { get; set; }
}
