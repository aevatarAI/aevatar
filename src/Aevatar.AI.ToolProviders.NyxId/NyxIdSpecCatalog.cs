using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdSpecCatalog : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly object _statusGate = new();
    private readonly NyxIdToolOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Timer? _refreshTimer;

    private OperationCard[] _catalog = [];
    private bool _initialRefreshAttempted;
    private bool _refreshInProgress;
    private DateTimeOffset? _lastSuccessfulRefreshUtc;
    private string? _lastRefreshError;
    private NyxIdSpecCatalogRefreshFailureKind? _lastRefreshFailureKind;

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

        _refreshTimer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, RefreshInterval);
    }

    public OperationCard[] Operations
    {
        get
        {
            lock (_statusGate)
                return _catalog;
        }
    }

    public NyxIdSpecCatalogStatus GetStatus()
    {
        lock (_statusGate)
        {
            return new NyxIdSpecCatalogStatus(
                BaseUrlConfigured: !string.IsNullOrWhiteSpace(_options.BaseUrl),
                SpecFetchTokenConfigured: !string.IsNullOrWhiteSpace(_options.SpecFetchToken),
                InitialRefreshAttempted: _initialRefreshAttempted,
                RefreshInProgress: _refreshInProgress,
                OperationCount: _catalog.Length,
                LastSuccessfulRefreshUtc: _lastSuccessfulRefreshUtc,
                LastRefreshError: _lastRefreshError,
                LastRefreshFailureKind: _lastRefreshFailureKind);
        }
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

    private async Task RefreshAsync()
    {
        if (!TryMarkRefreshStarted())
            return;

        try
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
        finally
        {
            MarkRefreshFinished();
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
            // Empty spec refreshes are soft failures: keep the last usable catalog
            // for execution, while status exposes the stale source condition.
            RememberRefreshFailure("Spec yielded no operations.", NyxIdSpecCatalogRefreshFailureKind.EmptySpec);
            _logger.LogWarning("NyxIdSpecCatalog: spec yielded no operations, preserving existing catalog");
            return;
        }

        lock (_statusGate)
        {
            _lastSuccessfulRefreshUtc = DateTimeOffset.UtcNow;
            _lastRefreshError = null;
            _lastRefreshFailureKind = null;
            _catalog = cards;
        }
        _logger.LogInformation("NyxIdSpecCatalog updated: {Count} operations", cards.Length);
    }

    private void RememberRefreshFailure(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;
        RememberRefreshFailure(message, ClassifyRefreshFailure(ex));
    }

    private void RememberRefreshFailure(
        string message,
        NyxIdSpecCatalogRefreshFailureKind failureKind)
    {
        lock (_statusGate)
        {
            _lastRefreshError = message;
            _lastRefreshFailureKind = failureKind;
        }
    }

    private bool TryMarkRefreshStarted()
    {
        lock (_statusGate)
        {
            if (_refreshInProgress)
                return false;

            _refreshInProgress = true;
            return true;
        }
    }

    private void MarkRefreshFinished()
    {
        lock (_statusGate)
        {
            _initialRefreshAttempted = true;
            _refreshInProgress = false;
        }
    }

    private static NyxIdSpecCatalogRefreshFailureKind ClassifyRefreshFailure(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            return NyxIdSpecCatalogRefreshFailureKind.Unauthorized;

        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            return NyxIdSpecCatalogRefreshFailureKind.Forbidden;

        if (ex is HttpRequestException { StatusCode: not null })
            return NyxIdSpecCatalogRefreshFailureKind.HttpError;

        if (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            return NyxIdSpecCatalogRefreshFailureKind.NetworkError;

        return NyxIdSpecCatalogRefreshFailureKind.Unexpected;
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
