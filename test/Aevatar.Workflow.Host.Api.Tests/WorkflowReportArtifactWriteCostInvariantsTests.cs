using System.Net;
using System.Text;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.Reflection;
using static Aevatar.Workflow.Host.Api.Tests.WorkflowReportArtifactWriteCostScenario;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Issue #3477 write invariants of the report artifact path, in the reduced shape (15 committed
/// events, one 64 KB request parameter): the real <see cref="WorkflowRunInsightReportArtifactProjector"/>
/// and the real <see cref="ElasticsearchProjectionDocumentStore{TReadModel,TKey}"/> mutator / optimistic
/// writer run against an in-process Elasticsearch stand-in that only counts requests and bytes. Nothing
/// here measures time; the 263-event write-time benchmark lives in the real-Elasticsearch integration
/// lane (<see cref="WorkflowReportArtifactWriteCostElasticsearchIntegrationTests"/>).
/// </summary>
public sealed class WorkflowReportArtifactWriteCostInvariantsTests
{
    private const int ReplaySampleCount = 4;

    [Fact]
    public async Task AppliedStream_IssuesOneGetAndOneConditionalPutPerEvent_AndAccountsBytes()
    {
        var scenario = ReducedShape();
        scenario.AssertStreamShape();
        using var harness = new FakeElasticsearchHarness();
        var eventCount = scenario.CommittedEventCount;

        foreach (var committed in scenario.Events)
            await harness.ProjectAsync(scenario, committed);

        var fake = harness.Elasticsearch;
        fake.DocumentGetCount.Should().Be(eventCount);
        fake.DocumentPutCount.Should().Be(eventCount);
        fake.CreatePutCount.Should().Be(1, "only the first write may use _create; every later write is a conditional PUT");
        fake.ConflictCount.Should().Be(0);
        harness.GraphWriter.UpsertCount.Should().Be(eventCount);

        // The one-time index lifecycle probes all happen before the first document request; after that
        // every applied event is exactly one document GET followed by exactly one document PUT.
        fake.IndexProbeCount.Should().BeGreaterThan(0);
        fake.RequestKinds.Should().HaveCount(fake.IndexProbeCount + eventCount * 2);
        fake.RequestKinds.Take(fake.IndexProbeCount).Should().OnlyContain(kind => kind == FakeRequestKind.IndexProbe);
        var documentRequests = fake.RequestKinds.Skip(fake.IndexProbeCount).ToArray();
        for (var index = 0; index < documentRequests.Length; index += 2)
        {
            documentRequests[index].Should().Be(FakeRequestKind.DocumentGet, $"event {index / 2 + 1} must start with a GET");
            documentRequests[index + 1].Should().Be(FakeRequestKind.DocumentPut, $"event {index / 2 + 1} must commit with one PUT");
        }

        // Byte accounting: bytes written is the sum of the PUT bodies; every GET after the first re-reads
        // the body the previous event wrote (plus the Elasticsearch hit envelope).
        fake.DocumentPutBodyBytes.Should().HaveCount(eventCount);
        fake.BytesWritten.Should().Be(fake.DocumentPutBodyBytes.Sum());
        fake.DocumentGetBodyBytes.Should().HaveCount(eventCount);
        fake.BytesRead.Should().Be(fake.DocumentGetBodyBytes.Sum());
        for (var index = 1; index < eventCount; index++)
        {
            fake.DocumentGetBodyBytes[index].Should().BeGreaterThan(
                fake.DocumentPutBodyBytes[index - 1],
                $"the GET for event {index + 1} must return the document event {index} wrote");
        }

        var storedBody = fake.GetStoredBody(RootActorId) ?? throw new InvalidOperationException("No report document stored.");
        ((long)Encoding.UTF8.GetByteCount(storedBody)).Should().Be(fake.DocumentPutBodyBytes[^1]);
    }

    [Fact]
    public async Task AppliedStream_GrowsDocumentMonotonically_AndStoresEachLargeParameterOnce()
    {
        var scenario = ReducedShape();
        using var harness = new FakeElasticsearchHarness();
        var documentBytesByVersion = new List<long>(scenario.CommittedEventCount);

        foreach (var committed in scenario.Events)
        {
            await harness.ProjectAsync(scenario, committed);
            documentBytesByVersion.Add(harness.StoredDocument().CalculateSize());
        }

        var fake = harness.Elasticsearch;
        AssertMonotonicNonDecreasing(fake.DocumentPutBodyBytes, "Elasticsearch PUT body bytes");
        AssertMonotonicNonDecreasing(documentBytesByVersion, "stored report protobuf bytes");

        var finalDocument = harness.StoredDocument();
        var shape = scenario.MeasureFinalDocument(finalDocument);
        shape.LargeParameterOccurrences.Should().Be(scenario.LargeParameterSteps.Count);

        // The request PUT retains one full sanitized value in immutable evidence. The latest step and
        // step.request timeline add only typed references, so JSON growth stays below two full copies.
        foreach (var committed in scenario.Events.Where(x => x.LargeParameter != null))
        {
            var index = (int)committed.Version - 1;
            var growth = fake.DocumentPutBodyBytes[index] - fake.DocumentPutBodyBytes[index - 1];
            growth.Should().BeGreaterThanOrEqualTo(
                committed.LargeParameter!.Value.Length,
                $"version {committed.Version} must persist the {committed.LargeParameter.Key} evidence once");
            growth.Should().BeLessThan(
                2L * committed.LargeParameter.Value.Length,
                $"version {committed.Version} must not embed a second full {committed.LargeParameter.Key} copy");
        }
    }

    [Fact]
    public async Task ReplayedAndOutOfOrderEvents_ReadOnly_NeverWriteOrAppend()
    {
        var scenario = ReducedShape();
        using var harness = new FakeElasticsearchHarness();
        foreach (var committed in scenario.Events)
            await harness.ProjectAsync(scenario, committed);

        var fake = harness.Elasticsearch;
        var finalDocument = harness.StoredDocument();
        var getsBefore = fake.DocumentGetCount;
        var putsBefore = fake.DocumentPutCount;
        var bytesWrittenBefore = fake.BytesWritten;
        var graphBefore = harness.GraphWriter.UpsertCount;

        // Replaying already-applied events: one GET each, zero PUTs, no growth. Only the exact head event
        // is a byte-identical Duplicate (which still refreshes the graph); every older replay is Stale and
        // is dropped before the graph phase.
        var replaySample = scenario.PickReplaySample(ReplaySampleCount);
        replaySample.Should().Contain(x => x.Version == scenario.CommittedEventCount);
        foreach (var committed in replaySample)
            await harness.ProjectAsync(scenario, committed);
        fake.DocumentGetCount.Should().Be(getsBefore + ReplaySampleCount);
        fake.DocumentPutCount.Should().Be(putsBefore);
        fake.BytesWritten.Should().Be(bytesWrittenBefore);
        harness.GraphWriter.UpsertCount.Should().Be(graphBefore + 1);
        AssertDocumentUnchanged(harness.StoredDocument(), finalDocument);

        // An older, never-seen event arriving after a newer one: one GET, no PUT, no append, no graph write.
        await harness.ProjectAsync(scenario, scenario.LateOutOfOrderEvent);
        fake.DocumentGetCount.Should().Be(getsBefore + ReplaySampleCount + 1);
        fake.DocumentPutCount.Should().Be(putsBefore);
        fake.BytesWritten.Should().Be(bytesWrittenBefore);
        harness.GraphWriter.UpsertCount.Should().Be(graphBefore + 1);
        AssertDocumentUnchanged(harness.StoredDocument(), finalDocument);
        fake.ConflictCount.Should().Be(0);
    }

    /// <summary>
    /// Real store + mutator + optimistic writer + projector over the in-process Elasticsearch stand-in.
    /// </summary>
    private sealed class FakeElasticsearchHarness : IDisposable
    {
        private readonly ElasticsearchProjectionDocumentStore<WorkflowRunInsightReportDocument, string> _store;
        private readonly WorkflowRunInsightReportArtifactProjector _projector;

        public FakeElasticsearchHarness()
        {
            Elasticsearch = new FakeElasticsearchHandler();
            var options = new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://fake-elasticsearch.invalid:9200"],
                AutoCreateIndex = true,
            };
            _store = new ElasticsearchProjectionDocumentStore<WorkflowRunInsightReportDocument, string>(
                options,
                new WorkflowRunInsightReportDocumentMetadataProvider().Metadata,
                keySelector: document => document.Id,
                keyFormatter: key => key,
                typeRegistry: TypeRegistry.Empty,
                httpMessageHandler: Elasticsearch);
            GraphWriter = new CountingGraphWriter();
            _projector = new WorkflowRunInsightReportArtifactProjector(_store, GraphWriter);
        }

        public FakeElasticsearchHandler Elasticsearch { get; }

        public CountingGraphWriter GraphWriter { get; }

        public ValueTask ProjectAsync(WorkflowReportArtifactWriteCostScenario scenario, CommittedEvent committed) =>
            _projector.ProjectAsync(scenario.Context, committed.Envelope);

        public WorkflowRunInsightReportDocument StoredDocument() =>
            ParseStoredDocument(
                Elasticsearch.GetStoredBody(RootActorId)
                ?? throw new InvalidOperationException("Fake Elasticsearch holds no report document."));

        public void Dispose() => _store.Dispose();
    }

    private enum FakeRequestKind
    {
        IndexProbe,
        DocumentGet,
        DocumentPut,
    }

    /// <summary>
    /// In-process Elasticsearch stand-in: answers the greenfield index lifecycle probes, stores the last
    /// PUT body per document key with <c>_seq_no</c>/<c>_primary_term</c>, serves it back on GET, enforces
    /// <c>_create</c> and <c>if_seq_no</c>/<c>if_primary_term</c> conditions with 409, and counts request
    /// kinds, bytes read (GET response bodies) and bytes written (PUT request bodies).
    /// </summary>
    private sealed class FakeElasticsearchHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, StoredDocument> _documents = new(StringComparer.Ordinal);

        public int DocumentGetCount { get; private set; }

        public int DocumentPutCount { get; private set; }

        public int CreatePutCount { get; private set; }

        public int IndexProbeCount { get; private set; }

        public int ConflictCount { get; private set; }

        public long BytesRead { get; private set; }

        public long BytesWritten { get; private set; }

        public List<long> DocumentPutBodyBytes { get; } = [];

        public List<long> DocumentGetBodyBytes { get; } = [];

        public List<FakeRequestKind> RequestKinds { get; } = [];

        public string? GetStoredBody(string key) =>
            _documents.TryGetValue(key, out var stored) ? stored.Body : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return Handle(request, requestBody);
        }

        private HttpResponseMessage Handle(HttpRequestMessage request, byte[]? requestBody)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Fake Elasticsearch request has no URI.");
            var path = uri.AbsolutePath.TrimStart('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (request.Method == HttpMethod.Head || path.StartsWith("_alias/", StringComparison.Ordinal))
                return IndexProbe(HttpStatusCode.NotFound, request.Method == HttpMethod.Head ? "" : "{}");

            if (segments.Length == 3 && segments[1] is "_doc" or "_create")
            {
                var key = Uri.UnescapeDataString(segments[2]);
                if (request.Method == HttpMethod.Get)
                    return DocumentGet(segments[0], key);
                if (request.Method == HttpMethod.Put)
                {
                    return DocumentPut(
                        key,
                        create: segments[1] == "_create",
                        uri.Query,
                        requestBody ?? throw new InvalidOperationException("Document PUT without a body."));
                }
            }

            if (request.Method == HttpMethod.Put && segments.Length == 1)
                return IndexProbe(HttpStatusCode.OK, """{"acknowledged":true}""");

            throw new InvalidOperationException($"Unexpected fake Elasticsearch request {request.Method} {uri}.");
        }

        private HttpResponseMessage IndexProbe(HttpStatusCode statusCode, string body)
        {
            IndexProbeCount++;
            RequestKinds.Add(FakeRequestKind.IndexProbe);
            return Json(statusCode, body);
        }

        private HttpResponseMessage DocumentGet(string index, string key)
        {
            DocumentGetCount++;
            RequestKinds.Add(FakeRequestKind.DocumentGet);
            string body;
            HttpStatusCode status;
            if (_documents.TryGetValue(key, out var stored))
            {
                status = HttpStatusCode.OK;
                body = $$"""{"_index":"{{index}}","_id":"{{key}}","_seq_no":{{stored.SeqNo}},"_primary_term":{{stored.PrimaryTerm}},"found":true,"_source":{{stored.Body}}}""";
            }
            else
            {
                status = HttpStatusCode.NotFound;
                body = $$"""{"_index":"{{index}}","_id":"{{key}}","found":false}""";
            }

            var bodyBytes = Encoding.UTF8.GetByteCount(body);
            BytesRead += bodyBytes;
            DocumentGetBodyBytes.Add(bodyBytes);
            return Json(status, body);
        }

        private HttpResponseMessage DocumentPut(string key, bool create, string query, byte[] requestBody)
        {
            DocumentPutCount++;
            RequestKinds.Add(FakeRequestKind.DocumentPut);
            _documents.TryGetValue(key, out var existing);

            if (create)
            {
                CreatePutCount++;
                if (existing != null)
                    return Conflict();
                return Store(key, requestBody, seqNo: 0, primaryTerm: 1, HttpStatusCode.Created, """{"result":"created"}""");
            }

            var (expectedSeqNo, expectedPrimaryTerm) = ParseConcurrencyCondition(query);
            if (existing == null || existing.SeqNo != expectedSeqNo || existing.PrimaryTerm != expectedPrimaryTerm)
                return Conflict();

            return Store(key, requestBody, existing.SeqNo + 1, existing.PrimaryTerm, HttpStatusCode.OK, """{"result":"updated"}""");
        }

        private HttpResponseMessage Store(
            string key,
            byte[] requestBody,
            long seqNo,
            long primaryTerm,
            HttpStatusCode status,
            string responseBody)
        {
            _documents[key] = new StoredDocument(Encoding.UTF8.GetString(requestBody), seqNo, primaryTerm);
            BytesWritten += requestBody.Length;
            DocumentPutBodyBytes.Add(requestBody.Length);
            return Json(status, responseBody);
        }

        private HttpResponseMessage Conflict()
        {
            ConflictCount++;
            return Json(HttpStatusCode.Conflict, """{"error":{"type":"version_conflict_engine_exception"}}""");
        }

        private static (long SeqNo, long PrimaryTerm) ParseConcurrencyCondition(string query)
        {
            long seqNo = -1;
            long primaryTerm = -1;
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                    continue;
                var name = pair[..separator];
                var value = pair[(separator + 1)..];
                if (name == "if_seq_no")
                    seqNo = long.Parse(value);
                else if (name == "if_primary_term")
                    primaryTerm = long.Parse(value);
            }

            return (seqNo, primaryTerm);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private sealed record StoredDocument(string Body, long SeqNo, long PrimaryTerm);
    }
}
