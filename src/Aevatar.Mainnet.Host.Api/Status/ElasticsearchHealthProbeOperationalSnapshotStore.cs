using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StatusDashboard;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Status;

internal sealed class ElasticsearchHealthProbeOperationalSnapshotStore :
    IHealthProbeOperationalSnapshotStore,
    IProjectionIndexReconcileTarget,
    IDisposable
{
    private const string IndexScope = "health-probe-operational-snapshots";

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));
    private static readonly JsonParser Parser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly HttpClient _httpClient;
    private readonly ElasticsearchIndexLifecycleManager _indexManager;
    private readonly DocumentIndexMetadata _metadata;
    private readonly ILogger _logger;

    public ElasticsearchHealthProbeOperationalSnapshotStore(
        IReadOnlyList<string>? endpoints,
        string? indexPrefix,
        int requestTimeoutMs,
        string? username,
        string? password,
        HttpMessageHandler? httpMessageHandler = null,
        ILogger<ElasticsearchHealthProbeOperationalSnapshotStore>? logger = null)
    {
        _logger = logger ?? NullLogger<ElasticsearchHealthProbeOperationalSnapshotStore>.Instance;
        IndexAlias = $"{NormalizeToken(indexPrefix, "aevatar")}-{IndexScope}";
        _metadata = new DocumentIndexMetadata(
            IndexAlias,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["dynamic"] = true },
            new Dictionary<string, object?>(StringComparer.Ordinal),
            new Dictionary<string, object?>(StringComparer.Ordinal));
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.BaseAddress = ResolvePrimaryEndpoint(endpoints);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, requestTimeoutMs));
        if (!string.IsNullOrWhiteSpace(username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }

        _indexManager = new ElasticsearchIndexLifecycleManager(_httpClient, autoCreate: true, _logger);
    }

    public string IndexAlias { get; }

    public Task ReconcileIndexAsync(CancellationToken ct = default) =>
        _indexManager.ReconcileWithReindexAsync(IndexAlias, _metadata, ct);

    public async Task UpsertAsync(
        HealthProbeOperationalSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Target?.Slug);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{IndexAlias}/_doc/{Uri.EscapeDataString(snapshot.Target.Slug)}")
        {
            Content = new StringContent(Formatter.Format(snapshot), Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "health snapshot upsert", ct);
        _logger.LogInformation(
            "Health probe operational snapshot overwritten. slug={Slug} index={IndexAlias}",
            snapshot.Target.Slug,
            IndexAlias);
    }

    public async Task<HealthProbeOperationalSnapshot?> GetAsync(
        string slug,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using var response = await _httpClient.GetAsync(
            $"{IndexAlias}/_doc/{Uri.EscapeDataString(slug.Trim())}",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var notFound = await response.Content.ReadAsStringAsync(ct);
            if (IsIndexNotFound(notFound))
                throw new InvalidOperationException($"Elasticsearch health snapshot index '{IndexAlias}' was not found.");
            return null;
        }

        await EnsureSuccessAsync(response, "health snapshot get", ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("_source", out var source)
            ? Parser.Parse<HealthProbeOperationalSnapshot>(source.GetRawText())
            : null;
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        _ = await response.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "Elasticsearch health snapshot operation failed. operation={Operation} statusCode={StatusCode}",
            operation,
            (int)response.StatusCode);
        throw new InvalidOperationException(
            $"Elasticsearch {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static bool IsIndexNotFound(string payload) =>
        payload.Contains("index_not_found_exception", StringComparison.OrdinalIgnoreCase);

    private static Uri ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
    {
        if (endpoints == null || endpoints.Count == 0 || string.IsNullOrWhiteSpace(endpoints[0]))
            throw new InvalidOperationException("Elasticsearch provider requires at least one endpoint.");

        var endpoint = endpoints[0].Trim();
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"Invalid Elasticsearch endpoint '{endpoints[0]}'.");
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        return normalized.Length == 0 ? fallback : normalized;
    }

    public void Dispose()
    {
        _indexManager.Dispose();
        _httpClient.Dispose();
    }
}
