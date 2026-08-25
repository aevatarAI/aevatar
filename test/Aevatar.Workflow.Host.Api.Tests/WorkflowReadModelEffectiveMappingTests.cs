using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Audits the effective Elasticsearch mapping of the workflow read models: the provider metadata
/// after normalization and descriptor augmentation, exactly as the store PUTs it when it creates
/// the fingerprinted physical index. No live Elasticsearch is needed; a scripted handler captures
/// the index-create request.
/// </summary>
public sealed class WorkflowReadModelEffectiveMappingTests
{
    // Pinned schema fingerprints. The physical index is '{alias}-v{fingerprint}' and the
    // fingerprint is a hash over the final mappings, so ANY mapping or descriptor change for these
    // documents must change the value below and rolls out through the startup reindex/alias swap
    // (ElasticsearchProjectionIndexReconcileHostedService); the request path fails closed on drift.
    private const string CurrentStatePhysicalIndex = "/aevatar-workflow-execution-current-states-v7654550d";
    private const string ReportPhysicalIndex = "/aevatar-workflow-execution-reports-vba382118";
    private const string ActorBindingPhysicalIndex = "/aevatar-workflow-actor-bindings-v8684cd7c";

    [Fact]
    public async Task CurrentStateDocument_EffectiveMapping_ShouldMapEveryQueriedFieldExplicitly()
    {
        var created = await CaptureIndexCreateAsync(
            new WorkflowExecutionCurrentStateDocumentMetadataProvider().Metadata,
            new WorkflowExecutionCurrentStateDocument { Id = "actor-1", RootActorId = "actor-1" },
            document => document.RootActorId);
        var properties = ReadMappingProperties(created.Body);

        created.PathAndQuery.Should().Be(CurrentStatePhysicalIndex);

        // Eq / In filters and sorts issued by WorkflowExecutionCurrentStateQueryPort,
        // WorkflowTerminalStateReconciler and NyxIdWorkflowAdmissionEnforcementStartupGuard.
        foreach (var field in new[]
                 {
                     "saga_status", "scope_id", "definition_actor_id", "status", "run_id", "workflow_id",
                     "run_origin", "schedule_id", "root_actor_id",
                 })
        {
            AssertKeyword(properties, field);
        }

        AssertDate(properties, "updated_at_utc_value");

        // ContainsText (wildcard) search fields stay searchable as keyword.
        AssertKeyword(properties, "workflow_name");
        AssertKeyword(properties, "input_summary");
        AssertKeyword(properties, "activity_initiator.display_value");
    }

    [Fact]
    public async Task CurrentStateDocument_EffectiveMapping_ShouldKeepPayloadOutOfTheIndex()
    {
        var created = await CaptureIndexCreateAsync(
            new WorkflowExecutionCurrentStateDocumentMetadataProvider().Metadata,
            new WorkflowExecutionCurrentStateDocument { Id = "actor-1", RootActorId = "actor-1" },
            document => document.RootActorId);
        var properties = ReadMappingProperties(created.Body);

        // Proto maps stay disabled objects (provider or descriptor augmenter).
        foreach (var field in new[]
                 {
                     "inline_workflow_yaml_entries",
                     "fork_seed_variable_entries",
                     "fork_seed_idempotency_entries",
                     "normalized_fork_seed.variables",
                     "normalized_fork_seed.canonical_values",
                     "normalized_fork_seed.bindings",
                     "normalized_fork_seed.completed_steps",
                     "normalized_fork_seed.pending_output_references",
                     "normalized_fork_seed.source_completion_value_ids",
                     "normalized_fork_seed.source_completions",
                     "normalized_fork_seed.released_bindings",
                 })
        {
            AssertDisabledObject(properties, field);
        }

        // Opaque scalar payload is stored but never indexed (index:false or inside a disabled object).
        foreach (var field in new[]
                 {
                     "workflow_yaml", "input", "final_output", "final_error", "compilation_error", "dead_letter_error",
                     "activity_current_step.input_summary",
                     "activity_first_failure.message",
                     "activity_waiting.prompt",
                     "recovery_capability.retry_failed_step.unavailable_reason",
                     "recovery_capability.run_again.unavailable_reason",
                     "lineage.unavailable_reason",
                     "input_file_ref_entries.file_name",
                     "connector_approval_entries.plan.summary",
                     "capability_admission_plan.definition_digest",
                 })
        {
            AssertNotIndexed(properties, field);
        }

        // Never-queried subtrees are disabled as a whole.
        AssertDisabledObject(properties, "input_file_ref_entries");
        AssertDisabledObject(properties, "connector_approval_entries");
        AssertDisabledObject(properties, "capability_admission_plan");

        AssertNoNestedType(properties, "");
    }

    [Fact]
    public async Task ReportDocument_EffectiveMapping_ShouldKeepMapsDisabledAndPayloadOutOfTheIndex()
    {
        var created = await CaptureIndexCreateAsync(
            new WorkflowRunInsightReportDocumentMetadataProvider().Metadata,
            new WorkflowRunInsightReportDocument { Id = "actor-1", RootActorId = "actor-1" },
            document => document.RootActorId);
        var properties = ReadMappingProperties(created.Body);

        created.PathAndQuery.Should().Be(ReportPhysicalIndex);

        foreach (var field in new[]
                 {
                     "step_entries.request_parameters_map",
                     "step_entries.completion_annotations_map",
                     "step_entries.request_evidence_reference",
                     "timeline_entries.data_map",
                     "timeline_entries.request_evidence_reference",
                     "step_index_by_id",
                     "request_evidence_by_id",
                     "summary_value.step_type_counts_map",
                     "step_entries.file_item_results",
                     "step_entries.vote_agreement_decision",
                     "step_entries.latest_failed_attempt",
                 })
        {
            AssertDisabledObject(properties, field);
        }

        foreach (var field in new[]
                 {
                     "input", "final_output", "final_error",
                     "step_entries.output_preview", "step_entries.error", "step_entries.assigned_value",
                     "step_entries.suspension_prompt", "step_entries.suspension_content", "step_entries.failure_output",
                     "role_reply_entries.content",
                     "timeline_entries.message",
                     "operation_entries.input_summary", "operation_entries.output", "operation_entries.error",
                     "operation_entries.arguments_json", "operation_entries.result_json",
                     "operation_entries.reasoning_content",
                 })
        {
            AssertNotIndexed(properties, field);
        }

        // Ids, enums and names inside nested entries are explicit keywords; timestamps are dates.
        foreach (var field in new[]
                 {
                     "report_version", "workflow_name", "usage_value.model",
                     "topology_entries.parent", "topology_entries.child",
                     "step_entries.step_id", "step_entries.target_role", "step_entries.assigned_variable",
                     "step_entries.requested_variable_name", "step_entries.display_name", "step_entries.outcome",
                     "step_entries.tool_approval_value.execution_id", "step_entries.tool_approval_value.tool_name",
                     "step_entries.tool_approval_value.tool_call_id",
                     "step_entries.tool_approval_value.approval_request_id",
                     "step_entries.usage_value.model",
                     "role_reply_entries.role_id", "role_reply_entries.session_id",
                     "timeline_entries.stage", "timeline_entries.event_type",
                     "operation_entries.session_id", "operation_entries.operation_id", "operation_entries.kind",
                     "operation_entries.role_actor_id", "operation_entries.tool_call_id", "operation_entries.tool_name",
                     "operation_entries.model", "operation_entries.provider", "operation_entries.finish_reason",
                     "operation_entries.available_tool_names", "operation_entries.usage_value.model",
                 })
        {
            AssertKeyword(properties, field);
        }

        foreach (var field in new[]
                 {
                     "role_reply_entries.timestamp_utc_value",
                     "operation_entries.started_at_utc_value",
                     "operation_entries.completed_at_utc_value",
                     "timeline_entries.timestamp_utc_value",
                 })
        {
            AssertDate(properties, field);
        }

        AssertNoNestedType(properties, "");
    }

    [Fact]
    public async Task ActorBindingDocument_EffectiveMapping_ShouldMapQueriedFieldsExplicitly()
    {
        var created = await CaptureIndexCreateAsync(
            new WorkflowActorBindingDocumentMetadataProvider().Metadata,
            new WorkflowActorBindingDocument { Id = "actor-1", ActorId = "actor-1" },
            document => document.ActorId);
        var properties = ReadMappingProperties(created.Body);

        created.PathAndQuery.Should().Be(ActorBindingPhysicalIndex);
        foreach (var field in new[] { "actor_id", "run_id", "scope_id", "definition_actor_id" })
            AssertKeyword(properties, field);
        ResolveMapping(properties, "actor_kind_value").GetProperty("type").GetString().Should().Be("integer");
        AssertDate(properties, "updated_at_utc_value");
        AssertNotIndexed(properties, "workflow_yaml");
        AssertDisabledObject(properties, "inline_workflow_yaml_entries");
        AssertDisabledObject(properties, "capability_admission_plan");
        AssertNoNestedType(properties, "");
    }

    [Fact]
    public async Task SagaStatusFilter_ShouldUseTheSerializedProtoEnumName_AndAgreeAcrossStores()
    {
        var deadLetter = new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-dead-letter",
            RootActorId = "actor-dead-letter",
            ScopeId = "scope-a",
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
        };
        var compensating = new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-compensating",
            RootActorId = "actor-compensating",
            ScopeId = "scope-a",
            SagaStatus = WorkflowSagaStatus.Compensating,
        };
        var serializedSagaStatus = ReadSerializedSagaStatus(deadLetter);
        serializedSagaStatus.Should().Be("WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER");

        // In-memory store: the query port's filter selects exactly the dead-letter document.
        var inMemoryStore = new InMemoryProjectionDocumentStore<WorkflowExecutionCurrentStateDocument, string>(
            document => document.RootActorId,
            key => key);
        await inMemoryStore.UpsertAsync(deadLetter);
        await inMemoryStore.UpsertAsync(compensating);
        var inMemoryPort = CreateQueryPort(inMemoryStore);

        var inMemorySnapshots = await inMemoryPort.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 10,
                ScopeId = "scope-a",
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            });

        inMemorySnapshots.Should().ContainSingle().Which.ActorId.Should().Be("actor-dead-letter");

        // Elasticsearch store: the same query emits a term filter on the explicit keyword field
        // carrying the serialized proto enum name, i.e. the value the index actually holds.
        var handler = new ScriptedHttpMessageHandler();
        var searchHits = "{\"hits\":{\"hits\":[{\"_id\":\"actor-dead-letter\",\"_source\":" +
                         SerializeDocument(deadLetter) + "}]}}";
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, searchHits));
        using var elasticsearchStore = new ElasticsearchProjectionDocumentStore<WorkflowExecutionCurrentStateDocument, string>(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                Endpoints = ["http://localhost:9200"],
            },
            new WorkflowExecutionCurrentStateDocumentMetadataProvider().Metadata,
            keySelector: document => document.RootActorId,
            keyFormatter: key => key,
            httpMessageHandler: handler);
        var elasticsearchPort = CreateQueryPort(elasticsearchStore);

        var elasticsearchSnapshots = await elasticsearchPort.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 10,
                ScopeId = "scope-a",
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            });

        elasticsearchSnapshots.Should().ContainSingle().Which.ActorId.Should().Be("actor-dead-letter");
        var search = handler.CapturedRequests.Should().ContainSingle().Subject;
        search.PathAndQuery.Should().EndWith("/_search");
        using var searchBody = JsonDocument.Parse(search.Body);
        var filters = searchBody.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter");
        var sagaTerm = filters.EnumerateArray()
            .Select(filter => filter.GetProperty("term"))
            .Single(term => term.TryGetProperty("saga_status", out _));
        sagaTerm.GetProperty("saga_status").GetString().Should().Be(serializedSagaStatus);
        search.Body.Should().NotContain("saga_status.keyword");
    }

    private static WorkflowExecutionCurrentStateQueryPort CreateQueryPort(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> reader) =>
        new(
            reader,
            new WorkflowExecutionReadModelMapper(),
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowActorCurrentStateQueryEnabled = true,
            });

    private static readonly JsonFormatter ProtoJsonFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));

    private static string SerializeDocument(IMessage document) => ProtoJsonFormatter.Format(document);

    private static string ReadSerializedSagaStatus(WorkflowExecutionCurrentStateDocument document)
    {
        using var json = JsonDocument.Parse(SerializeDocument(document));
        return json.RootElement.GetProperty("saga_status").GetString()!;
    }

    private static async Task<CapturedRequest> CaptureIndexCreateAsync<TDocument>(
        DocumentIndexMetadata metadata,
        TDocument document,
        Func<TDocument, string> keySelector)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"error":{"type":"alias_missing_exception"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, ""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

        using var store = new ElasticsearchProjectionDocumentStore<TDocument, string>(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
                Endpoints = ["http://localhost:9200"],
            },
            metadata,
            keySelector: keySelector,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(document);

        return handler.CapturedRequests.Single(request =>
            request.Method == "PUT" &&
            request.Body.Contains("\"mappings\"", StringComparison.Ordinal));
    }

    private static JsonElement ReadMappingProperties(string indexCreateBody)
    {
        using var json = JsonDocument.Parse(indexCreateBody);
        return json.RootElement.GetProperty("mappings").GetProperty("properties").Clone();
    }

    private static JsonElement ResolveMapping(JsonElement properties, string fieldPath)
    {
        var current = properties;
        var segments = fieldPath.Split('.');
        for (var index = 0; index < segments.Length; index++)
        {
            current.TryGetProperty(segments[index], out var mapping)
                .Should().BeTrue($"'{fieldPath}' must have an explicit mapping (missing segment '{segments[index]}')");
            if (index == segments.Length - 1)
                return mapping;

            mapping.TryGetProperty("properties", out current)
                .Should().BeTrue($"'{fieldPath}' must be reachable through explicit object properties");
        }

        throw new InvalidOperationException($"Unreachable for '{fieldPath}'.");
    }

    private static void AssertKeyword(JsonElement properties, string fieldPath)
    {
        var mapping = ResolveMapping(properties, fieldPath);
        mapping.GetProperty("type").GetString().Should().Be("keyword", $"'{fieldPath}' is filtered/searched/sorted");
        mapping.TryGetProperty("index", out _).Should().BeFalse($"'{fieldPath}' must stay searchable");
    }

    private static void AssertDate(JsonElement properties, string fieldPath)
    {
        ResolveMapping(properties, fieldPath).GetProperty("type").GetString()
            .Should().Be("date", $"'{fieldPath}' is range-filtered/sorted");
    }

    private static void AssertDisabledObject(JsonElement properties, string fieldPath)
    {
        var mapping = ResolveMapping(properties, fieldPath);
        mapping.GetProperty("type").GetString().Should().Be("object", $"'{fieldPath}' must be a disabled object");
        mapping.GetProperty("enabled").GetBoolean().Should().BeFalse($"'{fieldPath}' must be a disabled object");
    }

    /// <summary>
    /// The field is stored but not indexed: either it is mapped with <c>index:false</c>, or one of
    /// its ancestors is an <c>enabled:false</c> object.
    /// </summary>
    private static void AssertNotIndexed(JsonElement properties, string fieldPath)
    {
        var current = properties;
        var segments = fieldPath.Split('.');
        for (var index = 0; index < segments.Length; index++)
        {
            current.TryGetProperty(segments[index], out var mapping)
                .Should().BeTrue($"'{fieldPath}' must be explicitly kept out of the index (missing segment '{segments[index]}')");
            if (mapping.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean())
                return;

            if (index == segments.Length - 1)
            {
                mapping.TryGetProperty("index", out var indexed).Should().BeTrue($"'{fieldPath}' must be index:false");
                indexed.GetBoolean().Should().BeFalse($"'{fieldPath}' must be index:false");
                return;
            }

            mapping.TryGetProperty("properties", out current)
                .Should().BeTrue($"'{fieldPath}' must be reachable through explicit object properties");
        }
    }

    private static void AssertNoNestedType(JsonElement properties, string prefix)
    {
        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.TryGetProperty("type", out var type))
                type.GetString().Should().NotBe("nested", $"'{path}' must not use the nested type");

            if (property.Value.TryGetProperty("properties", out var children))
                AssertNoNestedType(children, path);
        }
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
