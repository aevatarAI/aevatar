using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.Reflection;
using Xunit.Abstractions;
using static Aevatar.Workflow.Host.Api.Tests.WorkflowReportArtifactWriteCostScenario;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Issue #3477 write-time benchmark against a real Elasticsearch: the production-shaped stream
/// (263 committed events, 130 steps, six 84 KB – 401 KB request parameters) is replayed through the
/// real <see cref="WorkflowRunInsightReportArtifactProjector"/> and the real
/// <see cref="ElasticsearchProjectionDocumentStore{TReadModel,TKey}"/> mutator / optimistic writer into
/// an isolated, per-run index. A pass-through <see cref="HttpClient"/> handler records every request's
/// real latency and body size; the table is printed through <see cref="ITestOutputHelper"/> and can
/// also be persisted as a CI artifact. Only the request/byte/document invariants are asserted; no
/// wall-clock bound is.
/// <para>
/// Environment: <c>AEVATAR_TEST_ELASTICSEARCH_ENDPOINT</c> (required, gates the test),
/// <c>AEVATAR_TEST_ELASTICSEARCH_USERNAME</c> / <c>AEVATAR_TEST_ELASTICSEARCH_PASSWORD</c> (optional basic auth),
/// <c>AEVATAR_TEST_WORKFLOW_REPORT_BENCHMARK_PATH</c> (optional durable text report).
/// </para>
/// </summary>
[Trait("Category", "ProviderIntegration")]
[Trait("Category", "Benchmark")]
[Trait("Feature", "ProjectionProviders")]
public sealed class WorkflowReportArtifactWriteCostElasticsearchIntegrationTests
{
    private const string EndpointVariable = "AEVATAR_TEST_ELASTICSEARCH_ENDPOINT";
    private const string UsernameVariable = "AEVATAR_TEST_ELASTICSEARCH_USERNAME";
    private const string PasswordVariable = "AEVATAR_TEST_ELASTICSEARCH_PASSWORD";
    private const string BenchmarkReportPathVariable = "AEVATAR_TEST_WORKFLOW_REPORT_BENCHMARK_PATH";
    private const int ElasticsearchRequestTimeoutMs = 30000;
    private static readonly TimeSpan CleanupClientTimeout = TimeSpan.FromSeconds(60);
    private const int ReplaySampleCount = 10;
    private const int ReportStride = 50;

    private readonly ITestOutputHelper _output;

    public WorkflowReportArtifactWriteCostElasticsearchIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [ElasticsearchIntegrationFact]
    public async Task ReportArtifactProjector_263EventLargeParameterRun_RecordsRealElasticsearchWriteCost()
    {
        var scenario = ProductionShape();
        scenario.AssertStreamShape();
        var endpoint = GetRequiredEnvironmentVariable(EndpointVariable);
        var username = Environment.GetEnvironmentVariable(UsernameVariable)?.Trim() ?? "";
        var password = Environment.GetEnvironmentVariable(PasswordVariable) ?? "";
        // The store builds the alias as "{prefix}-{metadata index name}" (lower-case tokens) and the
        // physical index as "{alias}-v{schema fingerprint}"; a per-run prefix isolates both.
        var indexPrefix = "aevatar-3477-" + Guid.NewGuid().ToString("N")[..12];
        var aliasName = $"{indexPrefix}-workflow-execution-reports";

        using var cleanupClient = CreateCleanupClient(endpoint, username, password);
        try
        {
            var result = await ReplayAgainstElasticsearchAsync(scenario, endpoint, username, password, indexPrefix);
            WriteReport(scenario, endpoint, aliasName, result);
        }
        finally
        {
            await DeleteIndicesAsync(cleanupClient, $"{indexPrefix}-*");
        }
    }

    private static async Task<ReplayResult> ReplayAgainstElasticsearchAsync(
        WorkflowReportArtifactWriteCostScenario scenario,
        string endpoint,
        string username,
        string password,
        string indexPrefix)
    {
        var elasticsearch = new RecordingElasticsearchHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            Endpoints = [endpoint],
            IndexPrefix = indexPrefix,
            AutoCreateIndex = true,
            RequestTimeoutMs = ElasticsearchRequestTimeoutMs,
            Username = username,
            Password = password,
        };
        using var store = new ElasticsearchProjectionDocumentStore<WorkflowRunInsightReportDocument, string>(
            options,
            new WorkflowRunInsightReportDocumentMetadataProvider().Metadata,
            keySelector: document => document.Id,
            keyFormatter: key => key,
            typeRegistry: TypeRegistry.Empty,
            httpMessageHandler: elasticsearch);

        // Create/reconcile the isolated physical index before event timing through the same
        // explicit startup lifecycle used by the host. A read intentionally does not bootstrap a
        // missing index, even when AutoCreateIndex is enabled, so invoking the reconcile target is
        // required before the warm-up GET. Both operations stay outside the applied stream and
        // cannot be mislabeled as per-event serialization/client time.
        var preparationStarted = Stopwatch.GetTimestamp();
        await ((IProjectionIndexReconcileTarget)store).ReconcileIndexAsync();
        (await store.GetAsync(RootActorId)).Should().BeNull();
        var indexPreparationElapsed = Stopwatch.GetElapsedTime(preparationStarted);
        var preparationRequests = elasticsearch.Requests.ToArray();
        var lifecycleRequests = preparationRequests.Where(x => x.Kind == RequestKind.IndexLifecycle).ToArray();
        lifecycleRequests.Should().NotBeEmpty();
        preparationRequests.Should().ContainSingle(x => x.Kind == RequestKind.DocumentGet);
        elasticsearch.ResetRecording();

        var timedMutator = new ReducerTimingMutator(store);
        var graphWriter = new CountingGraphWriter();
        var projector = new WorkflowRunInsightReportArtifactProjector(timedMutator, graphWriter);
        var eventCount = scenario.CommittedEventCount;

        // Phase 1: the applied stream.
        var eventMilliseconds = new List<double>(eventCount);
        var replayStarted = Stopwatch.GetTimestamp();
        foreach (var committed in scenario.Events)
        {
            var started = Stopwatch.GetTimestamp();
            await projector.ProjectAsync(scenario.Context, committed.Envelope);
            eventMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var replayElapsed = Stopwatch.GetElapsedTime(replayStarted);
        var streamRequests = elasticsearch.Requests.ToArray();
        var streamGets = streamRequests.Where(x => x.Kind == RequestKind.DocumentGet).ToArray();
        var streamPuts = streamRequests.Where(x => x.Kind == RequestKind.DocumentPut).ToArray();

        // Uncontended path: exactly one document GET followed by one conditional PUT per applied event;
        // index lifecycle work was completed and measured separately before this timing window.
        streamGets.Should().HaveCount(eventCount);
        streamPuts.Should().HaveCount(eventCount);
        streamPuts.Count(x => x.IsCreate).Should().Be(1);
        streamRequests.Should().OnlyContain(x => x.StatusCode != HttpStatusCode.Conflict);
        streamRequests.Should().HaveCount(eventCount * 2);
        streamRequests.Should().NotContain(x => x.Kind == RequestKind.IndexLifecycle);
        for (var index = 0; index < streamRequests.Length; index += 2)
        {
            streamRequests[index].Kind.Should().Be(RequestKind.DocumentGet, $"event {index / 2 + 1} must start with a GET");
            streamRequests[index + 1].Kind.Should().Be(RequestKind.DocumentPut, $"event {index / 2 + 1} must commit with one PUT");
        }

        graphWriter.UpsertCount.Should().Be(eventCount);
        var putBodyBytes = streamPuts.Select(x => x.RequestBytes).ToArray();
        AssertMonotonicNonDecreasing(putBodyBytes, "Elasticsearch PUT body bytes");
        var bytesWritten = putBodyBytes.Sum();
        var bytesRead = streamGets.Sum(x => x.ResponseBytes);

        var lastPutDocument = ParseStoredDocument(
            Encoding.UTF8.GetString(elasticsearch.LastDocumentPutBody
                                    ?? throw new InvalidOperationException("No document PUT was recorded.")));
        var storedDocument = await store.GetAsync(RootActorId)
                             ?? throw new InvalidOperationException("Elasticsearch returned no report document.");
        storedDocument.Should().Be(lastPutDocument, "the JSON round trip through Elasticsearch must reproduce the last committed document");
        var shape = scenario.MeasureFinalDocument(storedDocument);
        var finalGetRequest = elasticsearch.Requests[^1];
        finalGetRequest.Kind.Should().Be(RequestKind.DocumentGet);

        // Phase 2: replaying already-applied events is one GET each, zero PUTs, no growth. Only the exact
        // head event is a byte-identical Duplicate (which still refreshes the graph); older replays are Stale.
        var replaySample = scenario.PickReplaySample(ReplaySampleCount);
        var requestsBeforeReplay = elasticsearch.Requests.Count;
        var graphBeforeReplay = graphWriter.UpsertCount;
        var replayStartedAt = Stopwatch.GetTimestamp();
        foreach (var committed in replaySample)
            await projector.ProjectAsync(scenario.Context, committed.Envelope);
        var replaySampleElapsed = Stopwatch.GetElapsedTime(replayStartedAt);
        var replayRequests = elasticsearch.Requests.Skip(requestsBeforeReplay).ToArray();
        replayRequests.Should().HaveCount(ReplaySampleCount);
        replayRequests.Should().OnlyContain(x => x.Kind == RequestKind.DocumentGet);
        graphWriter.UpsertCount.Should().Be(graphBeforeReplay + replaySample.Count(x => x.Version == eventCount));
        var replayBytesRead = replayRequests.Sum(x => x.ResponseBytes);

        // Phase 3: an older, never-seen event arriving after the head is one GET, no PUT, no graph write.
        var requestsBeforeLate = elasticsearch.Requests.Count;
        var graphBeforeLate = graphWriter.UpsertCount;
        var lateStartedAt = Stopwatch.GetTimestamp();
        await projector.ProjectAsync(scenario.Context, scenario.LateOutOfOrderEvent.Envelope);
        var lateElapsed = Stopwatch.GetElapsedTime(lateStartedAt);
        var lateRequests = elasticsearch.Requests.Skip(requestsBeforeLate).ToArray();
        lateRequests.Should().ContainSingle().Which.Kind.Should().Be(RequestKind.DocumentGet);
        graphWriter.UpsertCount.Should().Be(graphBeforeLate);

        var unchanged = await store.GetAsync(RootActorId)
                        ?? throw new InvalidOperationException("Elasticsearch returned no report document.");
        AssertDocumentUnchanged(unchanged, storedDocument);

        return new ReplayResult(
            shape,
            lifecycleRequests.Length,
            indexPreparationElapsed,
            bytesRead,
            bytesWritten,
            putBodyBytes,
            streamGets.Select(x => x.ElapsedMilliseconds).ToArray(),
            streamPuts.Select(x => x.ElapsedMilliseconds).ToArray(),
            timedMutator.ReducerMilliseconds.Take(eventCount).ToArray(),
            eventMilliseconds,
            replayElapsed,
            finalGetRequest.ElapsedMilliseconds,
            replaySample.Count,
            replaySampleElapsed,
            replayBytesRead,
            lateElapsed);
    }

    private void WriteReport(
        WorkflowReportArtifactWriteCostScenario scenario,
        string endpoint,
        string aliasName,
        ReplayResult result)
    {
        var eventCount = scenario.CommittedEventCount;
        var shape = result.Shape;
        var getPercentiles = Percentiles(result.GetMilliseconds);
        var putPercentiles = Percentiles(result.PutMilliseconds);
        var reducerPercentiles = Percentiles(result.ReducerMilliseconds);
        var eventPercentiles = Percentiles(result.EventMilliseconds);
        var getTotal = result.GetMilliseconds.Sum();
        var putTotal = result.PutMilliseconds.Sum();
        var reducerTotal = result.ReducerMilliseconds.Sum();
        var eventTotal = result.EventMilliseconds.Sum();
        var derivedOverhead = eventTotal - getTotal - putTotal - reducerTotal;
        var largeVersions = scenario.LargeParameterRequestVersions;

        string Bytes(long value) => value.ToString("N0");
        string Ms(double value) => value.ToString("N0");
        string Pct(double part, double whole) => whole <= 0 ? "n/a" : (100.0 * part / whole).ToString("N1") + "%";
        string Row(IReadOnlyList<double> samples, (double P50, double P95, double Max) percentiles) =>
            $"{percentiles.P50:N2} / {percentiles.P95:N2} / {percentiles.Max:N2} (max @v{SlowestVersion(samples)})";
        string LargeRow(IReadOnlyList<double> samples) =>
            Ms(largeVersions.Sum(version => samples[(int)version - 1])) + " ms over " +
            string.Join(",", largeVersions.Select(version => $"v{version}"));

        var lines = new List<string>
        {
            $"== #3477 report-artifact write cost vs real Elasticsearch: {eventCount} committed events, {scenario.StepCount} steps, {scenario.LargeParameterSteps.Count} large parameters ==",
            $"{"endpoint / alias",-46} | {new Uri(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint).Host} / {aliasName}",
            $"{"applied events (1 GET + 1 conditional PUT each)",-46} | {eventCount}",
            $"{"index lifecycle requests / preparation wall ms",-46} | {result.IndexLifecycleRequestCount} / {Ms(result.IndexPreparationElapsed.TotalMilliseconds)}",
            $"{"bytes read (GET bodies) / written (PUT bodies)",-46} | {Bytes(result.BytesRead)} / {Bytes(result.BytesWritten)}",
            $"{"final doc protobuf bytes / JSON bytes (last PUT)",-46} | {Bytes(shape.TotalBytes)} / {Bytes(result.PutBodyBytes[^1])}",
            $"{"steps-only / timeline-only bytes (share)",-46} | {Bytes(shape.StepsBytes)} ({Pct(shape.StepsBytes, shape.TotalBytes)}) / {Bytes(shape.TimelineBytes)} ({Pct(shape.TimelineBytes, shape.TotalBytes)})",
            $"{"request evidence bytes (share)",-46} | {Bytes(shape.EvidenceBytes)} ({Pct(shape.EvidenceBytes, shape.TotalBytes)})",
            $"{"large parameter source bytes / stored copies",-46} | {Bytes(shape.LargeParameterBytes)} / {shape.LargeParameterOccurrences} copies (retained once)",
            $"{"ES GET ms p50 / p95 / max",-46} | {Row(result.GetMilliseconds, getPercentiles)}",
            $"{"ES PUT ms p50 / p95 / max",-46} | {Row(result.PutMilliseconds, putPercentiles)}",
            $"{"ES GET / PUT ms total",-46} | {Ms(getTotal)} / {Ms(putTotal)} ({Pct(getTotal + putTotal, eventTotal)} of per-event total)",
            $"{"ES PUT ms on large-parameter requests",-46} | {LargeRow(result.PutMilliseconds)}",
            $"{"reducer+clone ms p50 / p95 / max",-46} | {Row(result.ReducerMilliseconds, reducerPercentiles)}",
            $"{"reducer+clone ms total",-46} | {Ms(reducerTotal)} ({Pct(reducerTotal, eventTotal)} of per-event total)",
            $"{"reducer+clone ms on large-parameter requests",-46} | {LargeRow(result.ReducerMilliseconds)}",
            $"{"per-event projector ms p50 / p95 / max",-46} | {Row(result.EventMilliseconds, eventPercentiles)}",
            $"{"per-event projector ms total",-46} | {Ms(eventTotal)}",
            $"{"serialize/parse + client ms (derived)",-46} | {Ms(derivedOverhead)} ({Pct(derivedOverhead, eventTotal)} of per-event total)",
            $"{"stream replay ms (wall, all events)",-46} | {Ms(result.ReplayElapsed.TotalMilliseconds)}",
            $"{"final-size GET ms (store.GetAsync)",-46} | {Ms(result.FinalGetMilliseconds)}",
            $"{$"replay {result.ReplaySampleCount} applied events",-46} | +{result.ReplaySampleCount} GET, +0 PUT, {Ms(result.ReplaySampleElapsed.TotalMilliseconds)} ms, {Bytes(result.ReplayBytesRead)} bytes re-read",
            $"{"out-of-order older event",-46} | +1 GET, +0 PUT, {Ms(result.LateElapsed.TotalMilliseconds)} ms",
        };

        var putBytesLine = new StringBuilder("ES PUT body bytes by version:");
        var putMsLine = new StringBuilder("ES PUT ms by version:        ");
        var getMsLine = new StringBuilder("ES GET ms by version:        ");
        for (var version = ReportStride; version <= eventCount; version += ReportStride)
        {
            putBytesLine.Append($" v{version}={Bytes(result.PutBodyBytes[version - 1])}");
            putMsLine.Append($" v{version}={result.PutMilliseconds[version - 1]:N1}");
            getMsLine.Append($" v{version}={result.GetMilliseconds[version - 1]:N1}");
        }

        putBytesLine.Append($" v{eventCount}={Bytes(result.PutBodyBytes[^1])}");
        putMsLine.Append($" v{eventCount}={result.PutMilliseconds[^1]:N1}");
        getMsLine.Append($" v{eventCount}={result.GetMilliseconds[^1]:N1}");
        lines.Add(putBytesLine.ToString());
        lines.Add(putMsLine.ToString());
        lines.Add(getMsLine.ToString());

        foreach (var line in lines)
            _output.WriteLine(line);

        var reportPath = Environment.GetEnvironmentVariable(BenchmarkReportPathVariable)?.Trim() ?? string.Empty;
        if (reportPath.Length == 0)
            return;

        var fullReportPath = Path.GetFullPath(reportPath);
        var reportDirectory = Path.GetDirectoryName(fullReportPath);
        if (!string.IsNullOrWhiteSpace(reportDirectory))
            Directory.CreateDirectory(reportDirectory);
        File.WriteAllLines(fullReportPath, lines);
    }

    private static (double P50, double P95, double Max) Percentiles(IReadOnlyList<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double percentile) =>
            sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
        return (At(0.50), At(0.95), sorted[^1]);
    }

    /// <summary>Samples are indexed by stream order, so sample index + 1 is the committed version.</summary>
    private static long SlowestVersion(IReadOnlyList<double> samples)
    {
        var slowest = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index] > samples[slowest])
                slowest = index;
        }

        return slowest + 1;
    }

    // ---------------------------------------------------------------------------------------------
    // Environment and cleanup
    // ---------------------------------------------------------------------------------------------

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }

    private static HttpClient CreateCleanupClient(string endpoint, string username, string password)
    {
        var baseAddress = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint;
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress.TrimEnd('/') + "/"),
            Timeout = CleanupClientTimeout,
        };
        if (username.Length > 0)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return client;
    }

    /// <summary>
    /// Deletes every physical index matching <paramref name="indexPattern"/> by name (wildcard deletes
    /// are refused by clusters with <c>action.destructive_requires_name</c>); deleting the physical
    /// index drops its alias with it.
    /// </summary>
    private static async Task DeleteIndicesAsync(HttpClient client, string indexPattern)
    {
        using var listResponse = await client.GetAsync($"_cat/indices/{Uri.EscapeDataString(indexPattern)}?format=json&h=index");
        if (listResponse.StatusCode == HttpStatusCode.NotFound)
            return;
        var listPayload = await listResponse.Content.ReadAsStringAsync();
        listResponse.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch index resolution failed. body={listPayload}");

        using var document = JsonDocument.Parse(listPayload);
        var indices = document.RootElement.EnumerateArray()
            .Select(item => item.TryGetProperty("index", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        if (indices.Length == 0)
            return;

        using var deleteResponse = await client.DeleteAsync(string.Join(',', indices));
        var deletePayload = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch index cleanup failed. body={deletePayload}");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private sealed record ReplayResult(
        FinalDocumentShape Shape,
        int IndexLifecycleRequestCount,
        TimeSpan IndexPreparationElapsed,
        long BytesRead,
        long BytesWritten,
        IReadOnlyList<long> PutBodyBytes,
        IReadOnlyList<double> GetMilliseconds,
        IReadOnlyList<double> PutMilliseconds,
        IReadOnlyList<double> ReducerMilliseconds,
        IReadOnlyList<double> EventMilliseconds,
        TimeSpan ReplayElapsed,
        double FinalGetMilliseconds,
        int ReplaySampleCount,
        TimeSpan ReplaySampleElapsed,
        long ReplayBytesRead,
        TimeSpan LateElapsed);

    private enum RequestKind
    {
        IndexLifecycle,
        DocumentGet,
        DocumentPut,
    }

    private sealed record RecordedRequest(
        RequestKind Kind,
        bool IsCreate,
        HttpStatusCode StatusCode,
        double ElapsedMilliseconds,
        long RequestBytes,
        long ResponseBytes);

    /// <summary>Wraps a mutator to time the reducer the projector hands in, without altering its behavior.</summary>
    private sealed class ReducerTimingMutator
        : IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string>
    {
        private readonly IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> _inner;

        public ReducerTimingMutator(IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> inner)
        {
            _inner = inner;
        }

        public List<double> ReducerMilliseconds { get; } = [];

        public Task<ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>> MutateAsync(
            string key,
            Func<WorkflowRunInsightReportDocument?, WorkflowRunInsightReportDocument> reducer,
            CancellationToken ct = default) =>
            _inner.MutateAsync(
                key,
                existing =>
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        return reducer(existing);
                    }
                    finally
                    {
                        ReducerMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                },
                ct);
    }

    /// <summary>
    /// Pass-through handler in front of the real socket handler: classifies each request, measures its
    /// latency including full response-body transfer, and records request/response body sizes. It never
    /// alters a request or a response.
    /// </summary>
    private sealed class RecordingElasticsearchHandler : DelegatingHandler
    {
        public RecordingElasticsearchHandler()
            : base(new SocketsHttpHandler())
        {
        }

        public List<RecordedRequest> Requests { get; } = [];

        public byte[]? LastDocumentPutBody { get; private set; }

        public void ResetRecording()
        {
            Requests.Clear();
            LastDocumentPutBody = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (kind, isCreate) = Classify(request);
            var requestBody = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (kind == RequestKind.DocumentPut)
                LastDocumentPutBody = requestBody;

            var started = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var responseBytes = (await response.Content.ReadAsByteArrayAsync(cancellationToken)).LongLength;
            Requests.Add(new RecordedRequest(
                kind,
                isCreate,
                response.StatusCode,
                elapsed.TotalMilliseconds,
                requestBody?.LongLength ?? 0,
                responseBytes));
            return response;
        }

        private static (RequestKind Kind, bool IsCreate) Classify(HttpRequestMessage request)
        {
            var path = request.RequestUri?.AbsolutePath.TrimStart('/') ?? "";
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 3 && segments[1] is "_doc" or "_create")
            {
                if (request.Method == HttpMethod.Get)
                    return (RequestKind.DocumentGet, false);
                if (request.Method == HttpMethod.Put)
                    return (RequestKind.DocumentPut, segments[1] == "_create");
            }

            return (RequestKind.IndexLifecycle, false);
        }
    }
}
