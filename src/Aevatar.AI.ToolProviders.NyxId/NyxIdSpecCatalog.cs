using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdSpecCatalog : IDisposable
{
    private readonly NyxIdToolOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Timer? _refreshTimer;

    private OperationCard[] _catalog = [];
    private long _lastSuccessfulRefreshUnixTimeSeconds;
    private string? _lastRefreshError;

    public NyxIdSpecCatalog(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<NyxIdSpecCatalog>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<NyxIdSpecCatalog>.Instance;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return;

        if (string.IsNullOrWhiteSpace(_options.SpecFetchToken))
        {
            // NyxID's /api/v1/docs/openapi.json is human-only; without a token
            // every fetch returns 401. Skip the timer and surface this through
            // readiness so production does not run with an invisible empty catalog.
            _logger.LogWarning(
                "NyxIdSpecCatalog: SpecFetchToken not configured; skipping background refresh, catalog will remain empty");
            return;
        }

        _ = InitialFetchAsync();
        _refreshTimer = new Timer(_ => _ = RefreshAsync(), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public OperationCard[] Operations => Volatile.Read(ref _catalog);

    public NyxIdSpecCatalogStatus GetStatus()
    {
        var lastSuccessfulRefreshUnixTimeSeconds = Volatile.Read(ref _lastSuccessfulRefreshUnixTimeSeconds);
        var lastSuccessfulRefreshUtc = lastSuccessfulRefreshUnixTimeSeconds <= 0
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeSeconds(lastSuccessfulRefreshUnixTimeSeconds);

        return new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: !string.IsNullOrWhiteSpace(_options.BaseUrl),
            SpecFetchTokenConfigured: !string.IsNullOrWhiteSpace(_options.SpecFetchToken),
            OperationCount: Operations.Length,
            LastSuccessfulRefreshUtc: lastSuccessfulRefreshUtc,
            LastRefreshError: Volatile.Read(ref _lastRefreshError));
    }

    public IReadOnlyList<OperationCard> Search(string query, int maxResults = 5)
    {
        var snapshot = Operations;
        if (string.IsNullOrWhiteSpace(query) || snapshot.Length == 0)
            return [];

        var keywords = query.ToLowerInvariant()
            .Split([' ', '_', '-', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (keywords.Length == 0)
            return [];

        return snapshot
            .Select(op => (op, score: ScoreOperation(op, keywords)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => x.op)
            .ToList();
    }

    private static int ScoreOperation(OperationCard op, string[] keywords)
    {
        var score = 0;
        var opId = op.OperationId.ToLowerInvariant();
        var summary = op.Summary.ToLowerInvariant();
        var path = op.Path.ToLowerInvariant();

        foreach (var kw in keywords)
        {
            if (opId.Contains(kw)) score += 3;
            if (summary.Contains(kw)) score += 2;
            if (path.Contains(kw)) score += 1;
        }

        return score;
    }

    private async Task InitialFetchAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FetchAndUpdateAsync(cts.Token);
        }
        catch (Exception ex)
        {
            RememberRefreshFailure(ex);
            _logger.LogWarning(ex, "NyxIdSpecCatalog initial fetch failed, starting with empty catalog");
        }
    }

    private async Task RefreshAsync()
    {
        const int maxRetries = 3;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await FetchAndUpdateAsync(cts.Token);
                return;
            }
            catch (Exception ex)
            {
                RememberRefreshFailure(ex);
                _logger.LogWarning(ex, "NyxIdSpecCatalog refresh attempt {Attempt}/{Max} failed", i + 1, maxRetries);
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i + 1)));
            }
        }
    }

    private async Task FetchAndUpdateAsync(CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/docs/openapi.json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SpecFetchToken!);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var cards = OpenApiSpecParser.ParseSpec(json, "nyxid");

        if (cards.Length == 0)
        {
            Volatile.Write(ref _lastRefreshError, "Spec yielded no operations.");
            _logger.LogWarning("NyxIdSpecCatalog: spec yielded no operations, skipping update");
            return;
        }

        Volatile.Write(ref _catalog, cards);
        Volatile.Write(ref _lastRefreshError, null);
        Volatile.Write(ref _lastSuccessfulRefreshUnixTimeSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _logger.LogInformation("NyxIdSpecCatalog updated: {Count} operations", cards.Length);
    }

    private void RememberRefreshFailure(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;
        Volatile.Write(ref _lastRefreshError, message);
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
