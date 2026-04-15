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

    public NyxIdSpecCatalog(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<NyxIdSpecCatalog>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<NyxIdSpecCatalog>.Instance;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _ = InitialFetchAsync();
            _refreshTimer = new Timer(_ => _ = RefreshAsync(), null,
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }
    }

    public OperationCard[] Operations => Volatile.Read(ref _catalog);

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
        var specToken = _options.SpecFetchToken;
        if (!string.IsNullOrWhiteSpace(specToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", specToken);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var cards = OpenApiSpecParser.ParseSpec(json, "nyxid");

        if (cards.Length == 0)
        {
            _logger.LogWarning("NyxIdSpecCatalog: spec yielded no operations, skipping update");
            return;
        }

        Volatile.Write(ref _catalog, cards);
        _logger.LogInformation("NyxIdSpecCatalog updated: {Count} operations", cards.Length);
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
