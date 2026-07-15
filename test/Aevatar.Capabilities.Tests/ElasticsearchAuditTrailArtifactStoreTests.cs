using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Capabilities.Tests;

public sealed class ElasticsearchAuditTrailArtifactStoreTests
{
    [Fact]
    public async Task UpsertAsync_WhenDocumentIsNew_ShouldCreateIndexDocumentAndRoundTripWithGetAsync()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.Created, """{"result":"created"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(BuildDocument("audit-1", "hash-1"))));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(BuildDocument("audit-1", "hash-1"));
        var roundTripped = await store.GetAsync("audit-1");

        write.Disposition.Should().Be(AuditTrailArtifactWriteDisposition.Applied);
        roundTripped.Should().NotBeNull();
        roundTripped!.AuditId.Should().Be("audit-1");
        roundTripped.ContentHash.Should().Be("hash-1");
        handler.CapturedRequests.Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should()
            .Equal(
                "GET /audit-tests-audit-trail/_doc/audit-1",
                "PUT /audit-tests-audit-trail",
                "PUT /audit-tests-audit-trail/_create/audit-1",
                "GET /audit-tests-audit-trail/_doc/audit-1");

        var createRequest = handler.CapturedRequests.Single(static request =>
            request.PathAndQuery.EndsWith("/_create/audit-1", StringComparison.Ordinal));
        using var createBody = JsonDocument.Parse(createRequest.Body);
        createBody.RootElement.GetProperty("id").GetString().Should().Be("audit-1");
        var artifact = createBody.RootElement.GetProperty("artifact");
        artifact.GetProperty("audit_id").GetString().Should().Be("audit-1");
        artifact.GetProperty("content_hash").GetString().Should().Be("hash-1");
    }

    [Theory]
    [InlineData("hash-1", AuditTrailArtifactWriteDisposition.Duplicate)]
    [InlineData("hash-2", AuditTrailArtifactWriteDisposition.Conflict)]
    public async Task UpsertAsync_WhenDocumentAlreadyExists_ShouldReconcileByContentHash(
        string incomingContentHash,
        AuditTrailArtifactWriteDisposition expectedDisposition)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(BuildDocument("audit-1", "hash-1"))));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(BuildDocument("audit-1", incomingContentHash));

        write.Disposition.Should().Be(expectedDisposition);
        handler.CapturedRequests.Should().ContainSingle()
            .Which.PathAndQuery.Should().Be("/audit-tests-audit-trail/_doc/audit-1");
    }

    [Theory]
    [InlineData("hash-1", AuditTrailArtifactWriteDisposition.Duplicate)]
    [InlineData("hash-2", AuditTrailArtifactWriteDisposition.Conflict)]
    public async Task UpsertAsync_WhenCreateConflicts_ShouldFetchExistingAndReconcile(
        string existingContentHash,
        AuditTrailArtifactWriteDisposition expectedDisposition)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.Conflict, """{"result":"conflict"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(BuildDocument("audit-1", existingContentHash))));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(BuildDocument("audit-1", "hash-1"));

        write.Disposition.Should().Be(expectedDisposition);
        handler.CapturedRequests.Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should()
            .Equal(
                "GET /audit-tests-audit-trail/_doc/audit-1",
                "PUT /audit-tests-audit-trail",
                "PUT /audit-tests-audit-trail/_create/audit-1",
                "GET /audit-tests-audit-trail/_doc/audit-1");
    }

    [Fact]
    public async Task GetAsync_WhenIndexMissingAndAutoCreateDisabled_ShouldThrow()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false }, handler);

        var act = () => store.GetAsync("audit-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audit artifact index*not found*");
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchNewestAuditArtifactsAndReturnCursorAndWatermark()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            BuildSearchPayload(
                BuildDocument("audit-1", "hash-1"),
                """["2026-07-03T09:00:00Z","audit-1"]""")));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);

        var page = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery
        {
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:abc",
            IdentityKeyId = "identity-key-1",
            OperationName = "audit.test",
            Outcome = AuditOutcome.Success,
            OccurredFrom = DateTimeOffset.Parse("2026-07-03T00:00:00+00:00"),
            OccurredTo = DateTimeOffset.Parse("2026-07-04T00:00:00+00:00"),
            Take = 1,
        });

        page.Records.Should().ContainSingle()
            .Which.AuditId.Should().Be("audit-1");
        page.NextCursor.Should().NotBeNullOrWhiteSpace();
        page.Watermark.Should().Be(DateTimeOffset.Parse("2026-07-03T09:00:00Z"));
        handler.CapturedRequests.Should().ContainSingle()
            .Which.PathAndQuery.Should().Be("/audit-tests-audit-trail/_search");
        using var requestBody = JsonDocument.Parse(handler.CapturedRequests[0].Body);
        var filterJson = requestBody.RootElement
            .GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter")
            .GetRawText();
        filterJson.Should().Contain("artifact.scope_id.keyword");
        filterJson.Should().Contain("scope-1");
        filterJson.Should().Contain("artifact.audit_actor_id.keyword");
        filterJson.Should().Contain("audit_actor:abc");
        filterJson.Should().Contain("artifact.record.identity_key_id.keyword");
        filterJson.Should().Contain("identity-key-1");
        filterJson.Should().Contain("artifact.operation_name.keyword");
        filterJson.Should().Contain("audit.test");
        filterJson.Should().Contain("artifact.outcome.keyword");
        filterJson.Should().Contain("AUDIT_OUTCOME_SUCCESS");
        filterJson.Should().Contain("artifact.occurred_at");
        requestBody.RootElement.GetProperty("size").GetInt32().Should().Be(1);
        var sort = requestBody.RootElement.GetProperty("sort");
        sort[0].GetProperty("artifact.occurred_at").GetProperty("order").GetString().Should().Be("desc");
        sort[1].GetProperty("id.keyword").GetProperty("order").GetString().Should().Be("asc");
        requestBody.RootElement
            .GetProperty("aggs")
            .GetProperty("query_watermark")
            .GetProperty("max")
            .GetProperty("field")
            .GetString()
            .Should()
            .Be("artifact.occurred_at");
    }

    [Fact]
    public async Task QueryAsync_WhenCursorProvided_ShouldPassSearchAfter()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"hits":{"hits":[]}}"""));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("""["2026-07-03T09:00:00Z","audit-1"]"""));

        _ = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery
        {
            Cursor = cursor,
            Take = 10,
        });

        using var requestBody = JsonDocument.Parse(handler.CapturedRequests.Single().Body);
        requestBody.RootElement.GetProperty("search_after")[0].GetString()
            .Should().Be("2026-07-03T09:00:00Z");
        requestBody.RootElement.GetProperty("search_after")[1].GetString()
            .Should().Be("audit-1");
    }

    [Fact]
    public async Task UpsertAsync_WhenCreateFails_ShouldSurfaceHttpFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.InternalServerError, """{"error":"mapping failed"}"""));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false }, handler);

        var act = () => store.UpsertAsync(BuildDocument("audit-1", "hash-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audit artifact create failed*500*mapping failed*");
    }

    private static MainnetAgentProjectionDocumentStoresExtensions.ElasticsearchAuditTrailArtifactStore CreateStore(
        ElasticsearchProjectionDocumentStoreOptions options,
        HttpMessageHandler handler)
    {
        options.Endpoints = ["http://localhost:9200"];
        options.IndexPrefix = "audit-tests";
        return new MainnetAgentProjectionDocumentStoresExtensions.ElasticsearchAuditTrailArtifactStore(
            options,
            new AuditTrailDocumentMetadataProvider().Metadata,
            handler);
    }

    private static AuditTrailDocument BuildDocument(string auditId, string contentHash)
    {
        return new AuditTrailDocument
        {
            Id = auditId,
            AuditId = auditId,
            ContentHash = contentHash,
            OperationName = "audit.test",
            ScopeId = "scope-1",
            TargetKind = "test-target",
            TargetId = "target-1",
            Record = new AuditRecord
            {
                AuditId = auditId,
                OperationName = "audit.test",
                ScopeId = "scope-1",
                AuditActorId = "audit_actor:abc",
                IdentityKeyId = "identity-key-1",
                Outcome = AuditOutcome.Success,
                Target = new AuditTarget
                {
                    Kind = "test-target",
                    Id = "target-1",
                },
            },
            OccurredAtDateTimeOffset = DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            UpdatedAtDateTimeOffset = DateTimeOffset.Parse("2026-07-03T09:01:00+00:00"),
        };
    }

    private static string BuildHitPayload(AuditTrailDocument document)
    {
        var storageDocument = AuditTrailArtifactStorageDocument.FromArtifact(document);
        var formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));

        return "{\"_source\":" + formatter.Format(storageDocument) + "}";
    }

    private static string BuildSearchPayload(AuditTrailDocument document, string sortJson)
    {
        var storageDocument = AuditTrailArtifactStorageDocument.FromArtifact(document);
        var formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));

        return "{\"aggregations\":{\"query_watermark\":{\"value\":1783069200000,\"value_as_string\":\"2026-07-03T09:00:00.000Z\"}},\"hits\":{\"hits\":[{\"_source\":"
            + formatter.Format(storageDocument)
            + ",\"sort\":"
            + sortJson
            + "}]}}";
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responses.Enqueue(responseFactory);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            CapturedRequests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "",
                requestBody));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No scripted response available for request '{request.Method} {request.RequestUri}'.");
            }

            return _responses.Dequeue().Invoke(request);
        }
    }

    private sealed record CapturedRequest(string Method, string PathAndQuery, string Body);
}
