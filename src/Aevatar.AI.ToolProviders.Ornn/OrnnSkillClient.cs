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

    /// <summary>
    /// Default per-call timeout for Ornn HTTP fetches through the NyxID proxy. Without this, a
    /// stuck upstream call can hold an Orleans grain turn captive for minutes. Successful calls
    /// complete in ~1s, so 30s leaves generous headroom while surfacing the upstream failure
    /// quickly enough that the LLM can recover through skill-search/fallback instructions
    /// instead of silently waiting on one blocked HTTP request.
    /// </summary>
    public static readonly TimeSpan DefaultPerCallTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _perCallTimeout;

    public OrnnSkillClient(OrnnOptions options, NyxIdApiClient nyxApi, ILogger<OrnnSkillClient>? logger = null)
        : this(options, nyxApi, DefaultPerCallTimeout, logger)
    {
    }

    /// <summary>
    /// Test-friendly overload: allows injecting a shorter per-call timeout so timeout behavior
    /// can be verified deterministically without sleeping 30s. Production code should use the
    /// primary constructor and accept <see cref="DefaultPerCallTimeout"/>.
    /// </summary>
    public OrnnSkillClient(
        OrnnOptions options,
        NyxIdApiClient nyxApi,
        TimeSpan perCallTimeout,
        ILogger<OrnnSkillClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxApi = nyxApi ?? throw new ArgumentNullException(nameof(nyxApi));
        if (perCallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perCallTimeout), "Per-call timeout must be positive.");
        _perCallTimeout = perCallTimeout;
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

        var path = $"/api/v1/skill-search?query={Uri.EscapeDataString(query)}&mode={normalizedMode}&scope={Uri.EscapeDataString(normalizedScope)}&page={page}&pageSize={pageSize}";

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSearchResult { Items = [], Error = proxyError };

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSearchResult>>(response, JsonOptions);
            return envelope?.Data ?? new OrnnSearchResult { Items = [] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is a control-flow signal — let it propagate so the outer LLM
            // run can react instead of seeing a synthetic "no skills" result.
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Our per-call budget fired (caller didn't cancel). Distinguish from generic failure
            // so log dashboards surface upstream slowness as its own signal.
            _logger.LogWarning(
                "Ornn skill search exceeded {TimeoutSeconds}s per-call budget for query '{Query}'",
                (int)_perCallTimeout.TotalSeconds,
                query);
            return new OrnnSearchResult
            {
                Items = [],
                Error = $"Ornn skill search exceeded {(int)_perCallTimeout.TotalSeconds}s budget.",
            };
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
        var path = $"/api/v1/skills/{Uri.EscapeDataString(idOrName)}/json";

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out _))
                return null;

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSkillJson>>(response, JsonOptions);
            return envelope?.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is a control-flow signal — let it propagate so the outer LLM
            // run can react instead of seeing a synthetic "skill not found" result.
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Our per-call budget fired (caller didn't cancel). Distinguish from generic failure
            // so log dashboards surface upstream slowness as its own signal.
            _logger.LogWarning(
                "Ornn get skill exceeded {TimeoutSeconds}s per-call budget for '{IdOrName}'",
                (int)_perCallTimeout.TotalSeconds,
                idOrName);
            return null;
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
    /// concise actionable message instead of a JsonException about the wrapper shape.
    /// </summary>
    private bool TryUnwrapNyxIdProxyError(string response, out string detail)
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
                ? statusProp.GetInt32()
                : 0;

            // 404 here means NyxID could not resolve `_options.NyxIdSlug` to an upstream — either
            // the user has not bound an Ornn service to this slug, or the deployment's NyxID
            // catalog uses a different slug name. The LLM can recover by guiding the user to
            // bind the service or by retrying with a different slug; surface that hint instead
            // of a bare "status=404".
            detail = status == 404
                ? $"Ornn skill API not reachable: NyxID has no service bound to slug '{_options.NyxIdSlug}'. " +
                  "The user may need to connect their Ornn account via NyxID (nyxid_services action=create), " +
                  "or the deployment may need to override Aevatar:Ornn:NyxIdSlug."
                : $"NyxID proxy returned status={status}.";
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
