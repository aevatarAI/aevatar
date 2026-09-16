using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Capabilities.Tests;

public sealed class ElasticsearchAuditTrailArtifactStoreTests
{
    [Fact]
    public async Task ReconcileIndexAsync_WhenIndexMissingAndAutoCreateDisabled_ShouldProvisionVersionedAlias()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":"alias_missing"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                MissingIndexBehavior = ElasticsearchMissingIndexBehavior.Throw,
            },
            handler);

        store.Should().BeAssignableTo<IProjectionIndexReconcileTarget>();
        await ((IProjectionIndexReconcileTarget)(object)store).ReconcileIndexAsync();

        handler.CapturedRequests.Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should()
            .SatisfyRespectively(
                request => request.Should().Be("GET /_alias/audit-tests-audit-trail-current"),
                request => request.Should().Be("HEAD /audit-tests-audit-trail-current"),
                request => request.Should().Be("HEAD /audit-tests-audit-trail"),
                request => request.Should().StartWith("PUT /audit-tests-audit-trail-current-v"));

        using var createPayload = JsonDocument.Parse(handler.CapturedRequests[3].Body);
        var mappings = createPayload.RootElement.GetProperty("mappings");
        var artifactProperties = mappings.GetProperty("properties")
            .GetProperty("artifact")
            .GetProperty("properties");
        artifactProperties.GetProperty("schema_version")
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("keyword");
        artifactProperties.GetProperty("schema_version").TryGetProperty("fields", out _)
            .Should().BeFalse();
        artifactProperties.GetProperty("scope_id")
            .GetProperty("fields")
            .GetProperty("keyword")
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("keyword");
        var chatProperties = artifactProperties.GetProperty("record")
            .GetProperty("properties")
            .GetProperty("provenance")
            .GetProperty("properties")
            .GetProperty("chat")
            .GetProperty("properties");
        foreach (var field in new[]
                 {
                     "surface",
                     "conversation_id",
                     "turn_id",
                     "task_id",
                     "step_id",
                     "action_request_id",
                 })
        {
            chatProperties.GetProperty(field).GetProperty("type").GetString()
                .Should().Be("keyword");
        }
        createPayload.RootElement.GetProperty("aliases")
            .TryGetProperty("audit-tests-audit-trail-current", out _)
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenLegacyBareIndexHasDriftedMapping_ShouldReindexWithoutDeletingLegacyData()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":"alias_missing"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"timed_out":false,"failures":[],"created":3}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                MissingIndexBehavior = ElasticsearchMissingIndexBehavior.Throw,
            },
            handler);

        await ((IProjectionIndexReconcileTarget)(object)store).ReconcileIndexAsync();

        var reindexRequest = handler.CapturedRequests.Single(static request =>
            request.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal));
        using var reindexPayload = JsonDocument.Parse(reindexRequest.Body);
        reindexPayload.RootElement.GetProperty("conflicts").GetString().Should().Be("proceed");
        reindexPayload.RootElement.GetProperty("source").GetProperty("index").GetString()
            .Should().Be("audit-tests-audit-trail");
        reindexPayload.RootElement.GetProperty("dest").GetProperty("index").GetString()
            .Should().StartWith("audit-tests-audit-trail-current-v");

        var aliasRequest = handler.CapturedRequests.Single(static request => request.PathAndQuery == "/_aliases");
        aliasRequest.Body.Should().Contain("audit-tests-audit-trail-current");
        aliasRequest.Body.Should().NotContain("remove_index");
        aliasRequest.Body.Should().NotContain("\"remove\"");
        handler.CapturedRequests.Should().NotContain(static request =>
            request.Method == "DELETE" || request.PathAndQuery.Contains("_delete_by_query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenAliasPointsToDriftedPhysical_ShouldReindexAndRetainOldPhysical()
    {
        const string oldPhysical = "audit-tests-audit-trail-current-vold";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [oldPhysical] = new Dictionary<string, object?>
                {
                    ["aliases"] = new Dictionary<string, object?>
                    {
                        ["audit-tests-audit-trail-current"] = new Dictionary<string, object?>(),
                    },
                },
            })));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"timed_out":false,"failures":[],"created":3}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false }, handler);

        await ((IProjectionIndexReconcileTarget)(object)store).ReconcileIndexAsync();

        var aliasRequest = handler.CapturedRequests.Single(static request => request.PathAndQuery == "/_aliases");
        aliasRequest.Body.Should().Contain(oldPhysical);
        aliasRequest.Body.Should().Contain("\"remove\"");
        aliasRequest.Body.Should().NotContain("remove_index");
        handler.CapturedRequests.Should().NotContain(static request => request.Method == "DELETE");
    }

    [Fact]
    public async Task UpsertAsync_WhenDocumentIsNew_ShouldCreateIndexDocumentAndRoundTripWithGetAsync()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":"alias_missing"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
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
        handler.CapturedRequests.Should().HaveCount(7);
        handler.CapturedRequests.Take(4).Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should().Equal(
                "GET /audit-tests-audit-trail-current/_doc/audit-1",
                "GET /_alias/audit-tests-audit-trail-current",
                "HEAD /audit-tests-audit-trail-current",
                "HEAD /audit-tests-audit-trail");
        handler.CapturedRequests[4].Method.Should().Be("PUT");
        handler.CapturedRequests[4].PathAndQuery.Should().StartWith("/audit-tests-audit-trail-current-v");
        handler.CapturedRequests.Skip(5).Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should().Equal(
                "PUT /audit-tests-audit-trail-current/_create/audit-1",
                "GET /audit-tests-audit-trail-current/_doc/audit-1");

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
    public async Task UpsertAsync_WhenDocumentAlreadyExists_ShouldReconcileByHashOrSemanticContent(
        string incomingContentHash,
        AuditTrailArtifactWriteDisposition expectedDisposition)
    {
        var incoming = BuildDocument("audit-1", incomingContentHash);
        if (expectedDisposition == AuditTrailArtifactWriteDisposition.Conflict)
            incoming.Record.Correlation.RequestId = "different-request";

        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(BuildDocument("audit-1", "hash-1"))));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(incoming);

        write.Disposition.Should().Be(expectedDisposition);
        handler.CapturedRequests.Should().ContainSingle()
            .Which.PathAndQuery.Should().Be("/audit-tests-audit-trail-current/_doc/audit-1");
    }

    [Theory]
    [InlineData("hash-1", AuditTrailArtifactWriteDisposition.Duplicate)]
    [InlineData("hash-2", AuditTrailArtifactWriteDisposition.Conflict)]
    public async Task UpsertAsync_WhenCreateConflicts_ShouldFetchExistingAndReconcile(
        string existingContentHash,
        AuditTrailArtifactWriteDisposition expectedDisposition)
    {
        var existing = BuildDocument("audit-1", existingContentHash);
        if (expectedDisposition == AuditTrailArtifactWriteDisposition.Conflict)
            existing.Record.Correlation.RequestId = "different-request";

        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":"alias_missing"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.Conflict, """{"result":"conflict"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(existing)));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(BuildDocument("audit-1", "hash-1"));

        write.Disposition.Should().Be(expectedDisposition);
        handler.CapturedRequests.Should().HaveCount(7);
        handler.CapturedRequests.Take(4).Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should().Equal(
                "GET /audit-tests-audit-trail-current/_doc/audit-1",
                "GET /_alias/audit-tests-audit-trail-current",
                "HEAD /audit-tests-audit-trail-current",
                "HEAD /audit-tests-audit-trail");
        handler.CapturedRequests[4].PathAndQuery.Should().StartWith("/audit-tests-audit-trail-current-v");
        handler.CapturedRequests.Skip(5).Select(static request => $"{request.Method} {request.PathAndQuery}")
            .Should().Equal(
                "PUT /audit-tests-audit-trail-current/_create/audit-1",
                "GET /audit-tests-audit-trail-current/_doc/audit-1");
    }

    [Fact]
    public async Task UpsertAsync_WhenCreateRaceHasLegacyHashAndTraceOnlyDifference_ShouldReturnDuplicate()
    {
        var incoming = BuildDocument("audit-1", "incoming-legacy-hash");
        incoming.Record.Correlation.TraceId = "fedcba9876543210fedcba9876543210";
        incoming.Record.Correlation.SpanId = "fedcba9876543210";
        incoming.Record.Correlation.Traceparent =
            "00-fedcba9876543210fedcba9876543210-fedcba9876543210-01";
        incoming.Record.Correlation.Tracestate = "vendor=attempt-2";
        var raced = incoming.Clone();
        raced.ContentHash = "stored-legacy-hash";
        raced.Record.Correlation.TraceId = "0123456789abcdef0123456789abcdef";
        raced.Record.Correlation.SpanId = "0123456789abcdef";
        raced.Record.Correlation.Traceparent =
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";
        raced.Record.Correlation.Tracestate = "vendor=attempt-1";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":"alias_missing"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.Conflict, """{"result":"conflict"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(raced)));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(incoming);

        write.Disposition.Should().Be(AuditTrailArtifactWriteDisposition.Duplicate);
        handler.CapturedRequests.Should().HaveCount(7);
    }

    [Fact]
    public async Task UpsertAsync_WhenHashesDifferAndBusinessFieldChanges_ShouldReturnConflict()
    {
        var existing = BuildDocument("audit-1", "stored-legacy-hash");
        var incoming = BuildDocument("audit-1", "incoming-legacy-hash");
        incoming.Record.Correlation.RequestId = "different-request";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, BuildHitPayload(existing)));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var write = await store.UpsertAsync(incoming);

        write.Disposition.Should().Be(AuditTrailArtifactWriteDisposition.Conflict);
        handler.CapturedRequests.Should().ContainSingle();
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
    public async Task QueryAsync_ShouldSearchNewestAuditArtifactsAndReturnCursorAndCoverage()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            BuildSearchPayload(
                BuildDocument("audit-1", "hash-1"),
                """["2026-07-03T09:00:00Z","audit-1"]""",
                BuildDocument("audit-2", "hash-2"),
                """["2026-07-03T08:00:00Z","audit-2"]""")));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);

        var page = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery
        {
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:abc",
            IdentityKeyId = "identity-key-1",
            OperationName = "audit.test",
            Outcome = AuditOutcome.Success,
            LifecyclePhase = AuditLifecyclePhase.Terminal,
            TerminalOutcome = AuditTerminalOutcome.Succeeded,
            TraceId = "trace-1",
            CorrelationId = "correlation-1",
            OccurredFrom = DateTimeOffset.Parse("2026-07-03T00:00:00+00:00"),
            OccurredTo = DateTimeOffset.Parse("2026-07-04T00:00:00+00:00"),
            Take = 1,
        });

        page.Records.Should().ContainSingle()
            .Which.AuditId.Should().Be("audit-1");
        page.NextCursor.Should().NotBeNullOrWhiteSpace();
        page.Coverage.IngestionWatermark.Should().Be(DateTimeOffset.Parse("2026-07-03T09:01:00Z"));
        page.Coverage.Truncated.Should().BeTrue();
        page.Coverage.SchemaCompatibility.Should().Be(AuditSchemaCompatibility.Current);
        handler.CapturedRequests.Should().ContainSingle()
            .Which.PathAndQuery.Should().Be("/audit-tests-audit-trail-current/_search");
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
        filterJson.Should().Contain("artifact.lifecycle_phase.keyword");
        filterJson.Should().Contain("AUDIT_LIFECYCLE_PHASE_TERMINAL");
        filterJson.Should().Contain("artifact.terminal_outcome.keyword");
        filterJson.Should().Contain("AUDIT_TERMINAL_OUTCOME_SUCCEEDED");
        filterJson.Should().Contain("artifact.trace_id.keyword");
        filterJson.Should().Contain("artifact.correlation_id.keyword");
        filterJson.Should().Contain("artifact.occurred_at");
        requestBody.RootElement.GetProperty("size").GetInt32().Should().Be(2);
        var sort = requestBody.RootElement.GetProperty("sort");
        sort[0].GetProperty("artifact.occurred_at").GetProperty("order").GetString().Should().Be("desc");
        sort[1].GetProperty("id.keyword").GetProperty("order").GetString().Should().Be("asc");
        requestBody.RootElement
            .GetProperty("aggs")
            .GetProperty("ingestion")
            .GetProperty("aggs")
            .GetProperty("watermark")
            .GetProperty("max")
            .GetProperty("field")
            .GetString()
            .Should()
            .Be("artifact.recorded_at");
        var incompatibleSchemaFilter = requestBody.RootElement
            .GetProperty("aggs")
            .GetProperty("incompatible_schema_records")
            .GetProperty("filter")
            .GetRawText();
        incompatibleSchemaFilter.Should().Contain(AuditContractSemantics.CurrentSchemaVersion);
        incompatibleSchemaFilter.Should().Contain("artifact.schema_version");
        incompatibleSchemaFilter.Should().NotContain("artifact.schema_version.keyword");
    }

    [Fact]
    public async Task QueryAsync_WithChatFilters_ShouldUseOneTermsFilterBeforePagination()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"hits":{"hits":[]}}"""));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);

        _ = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery
        {
            AuditActorIds = [" actor-key-2 ", "actor-key-1", "actor-key-2", " "],
            RequireChatProvenance = true,
            ChatSurface = AuditChatSurface.WorkflowChat,
            ChatConversationId = " conversation-alpha ",
            TerminalOutcome = AuditTerminalOutcome.Failed,
            Take = 1,
        });

        using var requestBody = JsonDocument.Parse(handler.CapturedRequests.Single().Body);
        var filters = requestBody.RootElement
            .GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter");
        var termsFilters = filters.EnumerateArray()
            .Where(static filter => filter.TryGetProperty("terms", out _))
            .ToArray();

        termsFilters.Should().ContainSingle();
        termsFilters[0].GetProperty("terms")
            .GetProperty("artifact.audit_actor_id.keyword")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("actor-key-2", "actor-key-1");
        filters.EnumerateArray().Any(static filter =>
            filter.TryGetProperty("exists", out var exists) &&
            exists.GetProperty("field").GetString() == "artifact.record.provenance.chat.surface")
            .Should().BeTrue();
        filters.GetRawText().Should().ContainAll(
            "artifact.record.provenance.chat.surface",
            "AUDIT_CHAT_SURFACE_WORKFLOW_CHAT",
            "artifact.record.provenance.chat.conversation_id",
            "conversation-alpha",
            "artifact.terminal_outcome.keyword",
            "AUDIT_TERMINAL_OUTCOME_FAILED");
        requestBody.RootElement.GetProperty("size").GetInt32().Should().Be(2);
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
    public async Task QueryAsync_WhenAnyNonCurrentSchemaExists_ShouldReportIncompatible()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """
            {
              "aggregations": {
                "ingestion": { "watermark": { "value": null } },
                "incompatible_schema_records": { "doc_count": 1 },
                "legacy_schema_records": { "doc_count": 0 }
              },
              "hits": { "hits": [] }
            }
            """));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);

        var page = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery());

        page.Coverage.SchemaCompatibility.Should().Be(AuditSchemaCompatibility.Incompatible);
    }

    [Fact]
    public async Task QueryAsync_WhenNoRecordsMatch_ShouldReturnValidEmptyPage()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """
            {
              "aggregations": {
                "ingestion": { "watermark": { "value": null } },
                "incompatible_schema_records": { "doc_count": 0 },
                "legacy_schema_records": { "doc_count": 0 }
              },
              "hits": { "hits": [] }
            }
            """));
        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions(), handler);

        var page = await ((IAuditTrailQueryPort)store).QueryAsync(new AuditTrailQuery
        {
            ScopeId = "scope-1",
            OccurredFrom = DateTimeOffset.Parse("2100-01-01T00:00:00Z"),
            OccurredTo = DateTimeOffset.Parse("2100-01-02T00:00:00Z"),
            Take = 1,
        });

        page.Records.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
        page.Coverage.Truncated.Should().BeFalse();
        page.Coverage.SchemaCompatibility.Should().Be(AuditSchemaCompatibility.Current);
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
            .WithMessage("*audit artifact create failed*500*errorType=backend_rejected*");
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
            EventKind = "audit.test",
            SchemaVersion = "1.0",
            LifecyclePhase = AuditLifecyclePhase.Terminal,
            TerminalOutcome = AuditTerminalOutcome.Succeeded,
            Subject = "test-target/target-1",
            Source = "urn:aevatar:audit:test",
            ScopeId = "scope-1",
            TargetKind = "test-target",
            TargetId = "target-1",
            Record = new AuditRecord
            {
                AuditId = auditId,
                EventKind = "audit.test",
                Subject = "test-target/target-1",
                SchemaVersion = "1.0",
                Source = "urn:aevatar:audit:test",
                OperationName = "audit.test",
                ScopeId = "scope-1",
                AuditActorId = "audit_actor:abc",
                IdentityKeyId = "identity-key-1",
                Outcome = AuditOutcome.Success,
                LifecyclePhase = AuditLifecyclePhase.Terminal,
                TerminalOutcome = AuditTerminalOutcome.Succeeded,
                Target = new AuditTarget
                {
                    Kind = "test-target",
                    Id = "target-1",
                },
                Correlation = new AuditCorrelation { CorrelationId = "correlation-1" },
            },
            OccurredAtDateTimeOffset = DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T09:01:00+00:00")),
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

    private static string BuildSearchPayload(
        AuditTrailDocument firstDocument,
        string firstSortJson,
        AuditTrailDocument secondDocument,
        string secondSortJson)
    {
        var firstStorageDocument = AuditTrailArtifactStorageDocument.FromArtifact(firstDocument);
        var secondStorageDocument = AuditTrailArtifactStorageDocument.FromArtifact(secondDocument);
        var formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));

        return "{\"aggregations\":{\"ingestion\":{\"watermark\":{\"value\":1783069260000,\"value_as_string\":\"2026-07-03T09:01:00.000Z\"}},\"incompatible_schema_records\":{\"doc_count\":0},\"legacy_schema_records\":{\"doc_count\":0}},\"hits\":{\"hits\":[{\"_source\":"
            + formatter.Format(firstStorageDocument)
            + ",\"sort\":"
            + firstSortJson
            + "},{\"_source\":"
            + formatter.Format(secondStorageDocument)
            + ",\"sort\":"
            + secondSortJson
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
