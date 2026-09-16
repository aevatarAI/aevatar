using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRuntimeOperationProjectionTests
{
    private const string RunActorId = "run-actor-alpha";

    [Fact]
    public async Task ProjectAsync_ShouldUpsertEveryModelRoundAndToolCallUsingSourceTimes()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };
        var modelRoundZeroStarted = DateTimeOffset.Parse("2026-08-14T03:00:00.000+00:00");
        var modelRoundZeroCompleted = DateTimeOffset.Parse("2026-08-14T03:00:01.250+00:00");
        var toolStarted = DateTimeOffset.Parse("2026-08-14T03:00:01.300+00:00");
        var toolCompleted = DateTimeOffset.Parse("2026-08-14T03:00:01.800+00:00");
        var modelRoundOneStarted = DateTimeOffset.Parse("2026-08-14T03:00:01.900+00:00");
        var modelRoundOneCompleted = DateTimeOffset.Parse("2026-08-14T03:00:03.200+00:00");

        await ProjectOperationAsync(projector, context, 1, Operation(
            "model-0",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            modelRoundZeroStarted,
            progressSequence: 10,
            round: 0,
            model: "deepseek-chat",
            provider: "deepseek",
            inputSummary: "Find today's deployment status.",
            availableToolNames: ["status", "search"],
            toolCatalogPolicyVersion: WorkflowToolCatalogPolicies.CurrentVersion,
            toolCatalogProof: new WorkflowAgentTurnToolCatalogProof
            {
                Budget = new WorkflowAgentTurnToolCatalogBudgetProof
                {
                    MaximumToolCount = WorkflowToolCatalogPolicies.MaximumWorkflowToolCount,
                    MaximumSchemaBytes = WorkflowToolCatalogPolicies.MaximumWorkflowSchemaBytes,
                },
                ToolCount = 2,
                SchemaBytes = 384,
                CatalogDigest = "catalog-digest-alpha",
            }));
        await ProjectOperationAsync(projector, context, 2, Operation(
            "model-0",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            modelRoundZeroCompleted,
            progressSequence: 15,
            round: 0,
            model: "deepseek-chat",
            output: string.Empty,
            reasoningContent: "A status tool is required.",
            finishReason: "tool_calls",
            success: true,
            usage: new WorkflowUsageMetrics
            {
                PromptTokens = 12,
                CompletionTokens = 3,
                TotalTokens = 15,
                Model = "deepseek-chat",
            }));
        await ProjectOperationAsync(projector, context, 3, Operation(
            "call-status-1",
            WorkflowRuntimeOperationKind.Tool,
            WorkflowRuntimeOperationPhase.Started,
            toolStarted,
            progressSequence: 20,
            toolCallId: "call-status-1",
            toolName: "status"));
        await ProjectOperationAsync(projector, context, 4, Operation(
            "call-status-1",
            WorkflowRuntimeOperationKind.Tool,
            WorkflowRuntimeOperationPhase.Completed,
            toolCompleted,
            progressSequence: 25,
            success: true,
            toolCallId: "call-status-1",
            toolName: "status",
            argumentsJson: "{\"service\":\"api\"}",
            resultJson: "{\"healthy\":true}"));
        await ProjectOperationAsync(projector, context, 5, Operation(
            "model-1",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            modelRoundOneStarted,
            progressSequence: 30,
            round: 1,
            model: "deepseek-chat",
            provider: "deepseek",
            inputSummary: "Continue with the tool result."));
        await ProjectOperationAsync(projector, context, 6, Operation(
            "model-1",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            modelRoundOneCompleted,
            progressSequence: 35,
            round: 1,
            model: "deepseek-chat",
            output: "The API is healthy.",
            finishReason: "stop",
            success: true));

        store.Stored.Should().NotBeNull();
        var operations = store.Stored!.Operations;
        operations.Should().HaveCount(3, "start and end facts upsert the same stable operation identity");

        var firstModel = operations.Single(operation => operation.OperationId == "model-0");
        firstModel.Kind.Should().Be(WorkflowRuntimeOperationKind.Model);
        firstModel.ProgressSequence.Should().Be(10);
        firstModel.Round.Should().Be(0);
        firstModel.StartedAt.Should().Be(modelRoundZeroStarted);
        firstModel.CompletedAt.Should().Be(modelRoundZeroCompleted);
        firstModel.DurationMs.Should().Be(1250);
        firstModel.Provider.Should().Be("deepseek");
        firstModel.InputSummary.Should().Be("Find today's deployment status.");
        firstModel.AvailableToolNames.Should().Equal("search", "status");
        firstModel.ToolCatalogPolicyVersion.Should().Be(WorkflowToolCatalogPolicies.CurrentVersion);
        firstModel.ToolCatalogToolCount.Should().Be(2);
        firstModel.ToolCatalogSchemaBytes.Should().Be(384);
        firstModel.ToolCatalogDigest.Should().Be("catalog-digest-alpha");
        firstModel.Output.Should().BeEmpty("a tool-call-only model response is still a distinct operation");
        firstModel.ReasoningContent.Should().Be("A status tool is required.");
        firstModel.FinishReason.Should().Be("tool_calls");
        firstModel.Usage.TotalTokens.Should().Be(15);

        var tool = operations.Single(operation => operation.OperationId == "call-status-1");
        tool.Kind.Should().Be(WorkflowRuntimeOperationKind.Tool);
        tool.ProgressSequence.Should().Be(20);
        tool.StartedAt.Should().Be(toolStarted);
        tool.CompletedAt.Should().Be(toolCompleted);
        tool.DurationMs.Should().Be(500);
        tool.ArgumentsJson.Should().Be("{\"service\":\"api\"}");
        tool.ResultJson.Should().Be("{\"healthy\":true}");
        tool.Success.Should().BeTrue();

        var secondModel = operations.Single(operation => operation.OperationId == "model-1");
        secondModel.ProgressSequence.Should().Be(30);
        secondModel.Round.Should().Be(1);
        secondModel.StartedAt.Should().Be(modelRoundOneStarted);
        secondModel.CompletedAt.Should().Be(modelRoundOneCompleted);
        secondModel.DurationMs.Should().Be(1300);
        secondModel.Output.Should().Be("The API is healthy.");
    }

    [Fact]
    public async Task ProjectAsync_LegacyRoleReply_ShouldNotOverwriteTypedToolCompletion()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };

        await ProjectOperationAsync(projector, context, 1, Operation(
            "call-search-1",
            WorkflowRuntimeOperationKind.Tool,
            WorkflowRuntimeOperationPhase.Completed,
            DateTimeOffset.Parse("2026-08-14T04:00:00+00:00"),
            progressSequence: 8,
            success: true,
            toolCallId: "call-search-1",
            toolName: "search",
            argumentsJson: "{\"query\":\"safe typed query\"}",
            resultJson: "{\"source\":\"typed\"}"));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new WorkflowRoleReplyRecordedEvent
                {
                    RunId = "run-alpha",
                    SessionId = "session-alpha",
                    RoleActorId = "role-actor-alpha",
                    Content = "done",
                    ToolCalls =
                    {
                        new WorkflowRoleReplyToolCall
                        {
                            CallId = "call-search-1",
                            ToolName = "legacy-search",
                            ArgumentsJson = "{\"authorization\":\"legacy raw value\"}",
                            ResultJson = "{\"source\":\"legacy\"}",
                            Success = false,
                            Error = "legacy failure",
                        },
                    },
                }));

        var tool = store.Stored!.Operations.Should().ContainSingle().Subject;
        tool.ArgumentsJson.Should().Be("{\"query\":\"safe typed query\"}");
        tool.ResultJson.Should().Be("{\"source\":\"typed\"}");
        tool.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_WhenEitherEndpointIsMissing_ShouldLeaveDurationUnavailable()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };

        await ProjectOperationAsync(projector, context, 1, Operation(
            "model-start-only",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            DateTimeOffset.Parse("2026-08-14T05:00:00+00:00"),
            progressSequence: 1));
        await ProjectOperationAsync(projector, context, 2, Operation(
            "tool-end-only",
            WorkflowRuntimeOperationKind.Tool,
            WorkflowRuntimeOperationPhase.Completed,
            DateTimeOffset.Parse("2026-08-14T05:00:01+00:00"),
            progressSequence: 2,
            success: true,
            toolCallId: "tool-end-only",
            toolName: "status"));

        var operations = store.Stored!.Operations;
        operations.Single(operation => operation.OperationId == "model-start-only")
            .DurationMs.Should().BeNull();
        operations.Single(operation => operation.OperationId == "tool-end-only")
            .DurationMs.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_WhenCompletionArrivesBeforeStart_ShouldMergeBothPhaseTimes()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };
        var startedAt = DateTimeOffset.Parse("2026-08-14T05:30:00+00:00");
        var completedAt = startedAt.AddMilliseconds(750);

        await ProjectOperationAsync(projector, context, 1, Operation(
            "model-out-of-order",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            completedAt,
            progressSequence: 20,
            output: "completed first",
            success: true));
        await ProjectOperationAsync(projector, context, 2, Operation(
            "model-out-of-order",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            startedAt,
            progressSequence: 10,
            inputSummary: "started second"));

        var operation = store.Stored!.Operations.Should().ContainSingle().Subject;
        operation.ProgressSequence.Should().Be(10);
        operation.StartedProgressSequence.Should().Be(10);
        operation.CompletedProgressSequence.Should().Be(20);
        operation.StartedAt.Should().Be(startedAt);
        operation.CompletedAt.Should().Be(completedAt);
        operation.DurationMs.Should().Be(750);
        operation.InputSummary.Should().Be("started second");
        operation.Output.Should().Be("completed first");
    }

    [Fact]
    public async Task ProjectAsync_HigherSequenceWithoutEventTime_ShouldPreservePhaseTimes()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };
        var startedAt = DateTimeOffset.Parse("2026-08-14T05:45:00+00:00");
        var completedAt = startedAt.AddSeconds(2);

        await ProjectOperationAsync(projector, context, 1, Operation(
            "model-empty-time",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            startedAt,
            progressSequence: 10,
            inputSummary: "initial input"));
        await ProjectOperationAsync(projector, context, 2, Operation(
            "model-empty-time",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            completedAt,
            progressSequence: 20,
            output: "initial output",
            success: true));
        await ProjectOperationAsync(projector, context, 3, Operation(
            "model-empty-time",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            eventTime: null,
            progressSequence: 30,
            inputSummary: "newer input"));
        await ProjectOperationAsync(projector, context, 4, Operation(
            "model-empty-time",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            eventTime: null,
            progressSequence: 40,
            output: "newer output",
            success: true));

        var operation = store.Stored!.Operations.Should().ContainSingle().Subject;
        operation.StartedProgressSequence.Should().Be(30);
        operation.CompletedProgressSequence.Should().Be(40);
        operation.StartedAt.Should().Be(startedAt);
        operation.CompletedAt.Should().Be(completedAt);
        operation.DurationMs.Should().Be(2000);
        operation.InputSummary.Should().Be("newer input");
        operation.Output.Should().Be("newer output");
    }

    [Fact]
    public void RuntimeOperationDuration_WhenCompletionPredatesStart_ShouldBeUnavailable()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-14T06:00:01+00:00");
        var operation = new WorkflowRuntimeOperationReadModel
        {
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMilliseconds(-1),
        };

        operation.DurationMs.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_StalePhaseFacts_ShouldNotOverwriteNewerPayloadOrAnchorSequence()
    {
        var store = new RecordingReportStore();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            store,
            new RecordingGraphWriter());
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RunActorId,
            ProjectionKind = "workflow-execution-materialization",
        };

        await ProjectOperationAsync(projector, context, 1, Operation(
            "model-stable",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            DateTimeOffset.Parse("2026-08-14T07:00:00+00:00"),
            progressSequence: 20,
            model: "new-model",
            provider: "new-provider",
            inputSummary: "new input",
            availableToolNames: ["new-tool"]));
        await ProjectOperationAsync(projector, context, 2, Operation(
            "model-stable",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            DateTimeOffset.Parse("2026-08-14T07:00:02+00:00"),
            progressSequence: 30,
            model: "new-model",
            output: "new output",
            finishReason: "stop",
            success: true,
            usage: new WorkflowUsageMetrics { TotalTokens = 7 }));

        await ProjectOperationAsync(projector, context, 3, Operation(
            "model-stable",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Started,
            DateTimeOffset.Parse("2026-08-14T06:59:00+00:00"),
            progressSequence: 10,
            model: "stale-model",
            provider: "stale-provider",
            inputSummary: "stale input",
            availableToolNames: ["stale-tool"]));
        await ProjectOperationAsync(projector, context, 4, Operation(
            "model-stable",
            WorkflowRuntimeOperationKind.Model,
            WorkflowRuntimeOperationPhase.Completed,
            DateTimeOffset.Parse("2026-08-14T07:00:03+00:00"),
            progressSequence: 25,
            model: "stale-model",
            output: "stale output",
            finishReason: "stale",
            success: false,
            usage: new WorkflowUsageMetrics { TotalTokens = 99 }));

        var operation = store.Stored!.Operations.Should().ContainSingle().Subject;
        operation.ProgressSequence.Should().Be(20);
        operation.StartedProgressSequence.Should().Be(20);
        operation.CompletedProgressSequence.Should().Be(30);
        operation.StartedAt.Should().Be(DateTimeOffset.Parse("2026-08-14T07:00:00+00:00"));
        operation.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-08-14T07:00:02+00:00"));
        operation.Model.Should().Be("new-model");
        operation.Provider.Should().Be("new-provider");
        operation.InputSummary.Should().Be("new input");
        operation.AvailableToolNames.Should().Equal("new-tool");
        operation.Output.Should().Be("new output");
        operation.FinishReason.Should().Be("stop");
        operation.Success.Should().BeTrue();
        operation.Usage.TotalTokens.Should().Be(7);
    }

    private static ValueTask ProjectOperationAsync(
        WorkflowRunInsightReportArtifactProjector projector,
        WorkflowExecutionMaterializationContext context,
        long version,
        WorkflowRuntimeOperationRecordedEvent operation) =>
        projector.ProjectAsync(context, BuildCommittedEnvelope(version, operation));

    private static WorkflowRuntimeOperationRecordedEvent Operation(
        string operationId,
        WorkflowRuntimeOperationKind kind,
        WorkflowRuntimeOperationPhase phase,
        DateTimeOffset? eventTime,
        long progressSequence,
        int round = 0,
        string model = "",
        string provider = "",
        string inputSummary = "",
        IReadOnlyList<string>? availableToolNames = null,
        string output = "",
        string reasoningContent = "",
        string finishReason = "",
        bool success = false,
        WorkflowUsageMetrics? usage = null,
        string toolCallId = "",
        string toolName = "",
        string argumentsJson = "",
        string resultJson = "",
        string toolCatalogPolicyVersion = "",
        WorkflowAgentTurnToolCatalogProof? toolCatalogProof = null)
    {
        var operation = new WorkflowRuntimeOperationRecordedEvent
        {
            RunId = "run-alpha",
            SessionId = "session-alpha",
            OperationId = operationId,
            Kind = kind,
            Phase = phase,
            ProgressSequence = progressSequence,
            Round = round,
            RoleActorId = "role-actor-alpha",
            Model = model,
            Provider = provider,
            InputSummary = inputSummary,
            Output = output,
            ReasoningContent = reasoningContent,
            FinishReason = finishReason,
            Success = success,
            Usage = usage,
            ToolCallId = toolCallId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ResultJson = resultJson,
            ToolCatalogPolicyVersion = toolCatalogPolicyVersion,
            ToolCatalogProof = toolCatalogProof,
        };
        if (eventTime.HasValue)
            operation.EventTime = Timestamp.FromDateTimeOffset(eventTime.Value);
        if (availableToolNames != null)
            operation.AvailableToolNames.Add(availableToolNames);
        return operation;
    }

    private static EventEnvelope BuildCommittedEnvelope(long version, IMessage payload)
    {
        var projectionArrivalTime = DateTimeOffset.Parse("2026-08-14T06:00:00+00:00")
            .AddSeconds(version);
        return new EventEnvelope
        {
            Id = $"run-envelope-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(projectionArrivalTime),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RunActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = $"run-event-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(projectionArrivalTime),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(new WorkflowRunState
                {
                    RunId = "run-alpha",
                    Status = "running",
                }),
            }),
        };
    }

    private sealed class RecordingReportStore
        : IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>,
          IProjectionWriteDispatcher<WorkflowRunInsightReportDocument>,
          IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string>
    {
        public WorkflowRunInsightReportDocument? Stored { get; private set; }

        public Task<WorkflowRunInsightReportDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult(Stored);

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            CancellationToken ct = default)
        {
            Stored = readModel;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Duplicate());

        public Task<ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>> MutateAsync(
            string key,
            Func<WorkflowRunInsightReportDocument?, WorkflowRunInsightReportDocument> reducer,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = Stored?.Clone();
            var incoming = reducer(existing);
            var result = ProjectionWriteResultEvaluator.Evaluate(Stored, incoming);
            if (result.IsApplied)
                Stored = incoming.Clone();

            return Task.FromResult(new ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>(
                result,
                Stored?.Clone()));
        }
    }

    private sealed class RecordingGraphWriter : IProjectionGraphWriter<WorkflowRunInsightReportDocument>
    {
        public Task UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            string projectionKind,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
