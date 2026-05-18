using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdSpecCatalog : IDisposable
{
    public const string HttpClientName = "nyxid-spec-catalog";

    private readonly NyxIdToolOptions _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _manualHttpClient;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _refreshCts = new();
    private readonly Task? _refreshLoop;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    private OperationCard[] _catalog = [];

    public NyxIdSpecCatalog(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<NyxIdSpecCatalog>? logger = null)
    {
        _options = options;
        // Refactor (iter10/cluster-019):
        // Old: singleton catalog owned an ambiguous raw HttpClient and a Timer callback.
        // New: singleton state uses IHttpClientFactory named clients in DI; manual construction owns only its fallback client.
        _manualHttpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _logger = logger ?? NullLogger<NyxIdSpecCatalog>.Instance;

        _refreshLoop = StartRefreshLoopIfConfigured();
    }

    [ActivatorUtilitiesConstructor]
    public NyxIdSpecCatalog(
        NyxIdToolOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<NyxIdSpecCatalog>? logger = null)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<NyxIdSpecCatalog>.Instance;

        // Refactor (iter10/cluster-019):
        // Old: singleton catalog pinned one HttpClient and only disposed the Timer.
        // New: singleton catalog owns only catalog state; named clients come from IHttpClientFactory per refresh.
        _refreshLoop = StartRefreshLoopIfConfigured();
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

    private Task? StartRefreshLoopIfConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return null;

        if (string.IsNullOrWhiteSpace(_options.SpecFetchToken))
        {
            // NyxID's /api/v1/docs/openapi.json is human-only; without a token
            // every fetch returns 401. Skip the timer to avoid 30-min noise.
            _logger.LogInformation(
                "NyxIdSpecCatalog: SpecFetchToken not configured; skipping background refresh, catalog will remain empty");
            return null;
        }

        return Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        await InitialFetchAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task InitialFetchAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await FetchAndUpdateAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxIdSpecCatalog initial fetch failed, starting with empty catalog");
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await FetchAndUpdateAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NyxIdSpecCatalog refresh attempt {Attempt}/{Max} failed", i + 1, maxRetries);
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i + 1)), ct);
            }
        }
    }

    private async Task FetchAndUpdateAsync(CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/docs/openapi.json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SpecFetchToken!);

        var (http, disposeHttpClient) = CreateHttpClient();
        try
        {
            using var response = await http.SendAsync(request, ct);
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
        finally
        {
            if (disposeHttpClient)
                http.Dispose();
        }
    }

    private (HttpClient Client, bool DisposeClient) CreateHttpClient()
    {
        if (_httpClientFactory is not null)
            return (_httpClientFactory.CreateClient(HttpClientName), true);

        return (_manualHttpClient ?? throw new ObjectDisposedException(nameof(NyxIdSpecCatalog)), false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _refreshCts.Cancel();
        if (_refreshLoop is null)
        {
            _refreshCts.Dispose();
            if (_ownsHttpClient)
                _manualHttpClient?.Dispose();
            return;
        }

        _ = DisposeAfterRefreshLoopAsync();
    }

    private async Task DisposeAfterRefreshLoopAsync()
    {
        try
        {
            await _refreshLoop!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _refreshCts.Dispose();
            if (_ownsHttpClient)
                _manualHttpClient?.Dispose();
        }
    }
}
