using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn Web API HTTP 客户端。</summary>
public sealed class OrnnSkillClient
{
    private readonly HttpClient _http;
    private readonly OrnnOptions _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OrnnSkillClient(OrnnOptions options, HttpClient? httpClient = null, ILogger<OrnnSkillClient>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<OrnnSkillClient>.Instance;
    }

    /// <summary>搜索技能。</summary>
    public async Task<OrnnSearchResult> SearchSkillsAsync(
        string accessToken,
        string query = "",
        string scope = "mixed",
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var baseUrl = _options.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new OrnnSearchResult { Items = [] };

        var url = $"{baseUrl}/api/web/skill-search?query={Uri.EscapeDataString(query)}&mode=keyword&scope={Uri.EscapeDataString(scope)}&page={page}&pageSize={pageSize}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<OrnnApiResponse<OrnnSearchResult>>(JsonOptions, ct);
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
        var baseUrl = _options.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var url = $"{baseUrl}/api/web/skills/{Uri.EscapeDataString(idOrName)}/json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<OrnnApiResponse<OrnnSkillJson>>(JsonOptions, ct);
            return envelope?.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn get skill failed for '{IdOrName}'", idOrName);
            return null;
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
