using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowExecutionBoardMaterializerTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeBoardStateFromCommittedEvents()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-alpha",
                    WorkflowName = "workflow-alpha",
                    DefinitionActorId = "definition-alpha",
                    ScopeId = "scope-alpha",
                    Input = "raw input must not become board preview",
                },
                BuildState("running"),
                version: 1,
                eventId: "evt-start",
                observedAt: "2026-06-24T13:00:00Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "node-alpha",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    Input = "raw node input must not become preview",
                },
                BuildState("running"),
                version: 2,
                eventId: "evt-request",
                observedAt: "2026-06-24T13:00:05Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new WaitingForSignalEvent
                {
                    RunId = "run-alpha",
                    StepId = "node-alpha",
                    SignalName = "approve",
                    TimeoutMs = 120000,
                },
                BuildState("running"),
                version: 3,
                eventId: "evt-wait",
                observedAt: "2026-06-24T13:00:10Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepCompletedEvent
                {
                    RunId = "run-alpha",
                    StepId = "node-alpha",
                    Success = true,
                    Output = "raw provider response must not become preview",
                    NextStepId = "node-beta",
                    BranchKey = "approved",
                },
                BuildState("running"),
                version: 4,
                eventId: "evt-alpha-complete",
                observedAt: "2026-06-24T13:00:45Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "node-beta",
                    StepType = "tool_call",
                },
                BuildState("running"),
                version: 5,
                eventId: "evt-beta-request",
                observedAt: "2026-06-24T13:01:00Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepCompletedEvent
                {
                    RunId = "run-alpha",
                    StepId = "node-beta",
                    Success = false,
                    Error = "System.InvalidOperationException: sensitive stack trace",
                },
                BuildState("failed", finalError: "workflow failed"),
                version: 6,
                eventId: "evt-beta-failed",
                observedAt: "2026-06-24T13:01:07Z"));

        store.Upserts.Should().HaveCount(6);
        var document = store.Stored["root-actor"];
        document.Id.Should().Be("root-actor");
        document.ActorId.Should().Be("root-actor");
        document.RootActorId.Should().Be("root-actor");
        document.StateVersion.Should().Be(6);
        document.LastEventId.Should().Be("evt-beta-failed");
        document.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:01:07Z"));
        document.CommandId.Should().Be("cmd-alpha");
        document.DefinitionActorId.Should().Be("definition-alpha");
        document.RunId.Should().Be("run-alpha");
        document.WorkflowName.Should().Be("workflow-alpha");
        document.ScopeId.Should().Be("scope-alpha");
        document.CompletionStatus.Should().Be(WorkflowExecutionBoardCompletionStatus.Failed);
        document.CurrentNodeId.Should().BeEmpty();
        document.LastNodeUpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:01:07Z"));
        document.Summary.CompletedSteps.Should().Be(1);
        document.Summary.RunningNodes.Should().Be(0);
        document.Summary.WaitingOrPendingNodes.Should().Be(0);
        document.Summary.FailedNodes.Should().Be(1);
        document.NodeEntries.Should().HaveCount(2);

        var alpha = document.NodeEntries.Single(node => node.NodeId == "node-alpha");
        alpha.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Completed);
        alpha.StepType.Should().Be("llm_call");
        alpha.RequestedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:05Z"));
        alpha.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:45Z"));
        alpha.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:45Z"));
        alpha.DurationMs.Should().Be(40000);
        alpha.NextNodeId.Should().Be("node-beta");
        alpha.BranchKey.Should().Be("approved");

        var beta = document.NodeEntries.Single(node => node.NodeId == "node-beta");
        beta.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Failed);
        beta.RequestedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:01:00Z"));
        beta.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:01:07Z"));
        beta.DurationMs.Should().Be(7000);

        WorkflowExecutionBoardDocument.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .NotContain(["safe_preview", "safe_error_summary"]);
        WorkflowExecutionBoardNodeReadModel.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .NotContain(["safe_output_preview", "safe_error_summary", "progress_completed", "progress_total"]);
    }

    [Fact]
    public async Task ProjectAsync_ShouldReopenRetriedNodeFromStepRequestEvent()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        await materializer.ProjectAsync(
            context,
            WrapCommitted(new StepRequestEvent { RunId = "run-alpha", StepId = "node-alpha", StepType = "llm_call" },
                BuildState("running"), 1, "evt-request-1", "2026-06-24T13:00:00Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(new StepCompletedEvent { RunId = "run-alpha", StepId = "node-alpha", Success = false },
                BuildState("failed"), 2, "evt-failed-1", "2026-06-24T13:00:30Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(new StepRequestEvent { RunId = "run-alpha", StepId = "node-alpha", StepType = "llm_call" },
                BuildState("running"), 3, "evt-request-2", "2026-06-24T13:01:00Z"));

        var document = store.Stored["root-actor"];
        document.CompletionStatus.Should().Be(WorkflowExecutionBoardCompletionStatus.Running);
        document.CurrentNodeId.Should().Be("node-alpha");
        document.Summary.RunningNodes.Should().Be(1);
        document.Summary.FailedNodes.Should().Be(0);

        var node = document.NodeEntries.Should().ContainSingle().Subject;
        node.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Running);
        node.RequestedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:01:00Z"));
        node.CompletedAt.Should().BeNull();
        node.DurationMs.Should().Be(0);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeWaitingNodeFromCommittedWaitSignalState()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "approval",
                    StepType = "wait_signal",
                },
                BuildState("running"),
                version: 1,
                eventId: "evt-request",
                observedAt: "2026-06-24T13:00:00Z"));
        var waitState = new WaitSignalModuleState();
        waitState.Pending.Add("run-alpha:approved:approval", new PendingSignalState
        {
            RunId = "run-alpha",
            StepId = "approval",
            SignalName = "approved",
        });

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = "wait_signal",
                    State = Any.Pack(waitState),
                },
                BuildState("running"),
                version: 2,
                eventId: "evt-wait-state",
                observedAt: "2026-06-24T13:00:05Z"));

        var document = store.Stored["root-actor"];
        document.CompletionStatus.Should().Be(WorkflowExecutionBoardCompletionStatus.WaitingForSignal);
        document.CurrentNodeId.Should().Be("approval");
        document.Summary.RunningNodes.Should().Be(0);
        document.Summary.WaitingOrPendingNodes.Should().Be(1);

        var node = document.NodeEntries.Should().ContainSingle().Subject;
        node.NodeId.Should().Be("approval");
        node.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Waiting);
        node.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:05Z"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeWaitingNodeFromCommittedStateRoot()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "approval",
                    StepType = "wait_signal",
                },
                BuildState("running"),
                version: 1,
                eventId: "evt-request",
                observedAt: "2026-06-24T13:00:00Z"));
        var waitState = new WaitSignalModuleState();
        waitState.Pending.Add("run-alpha:approved:approval", new PendingSignalState
        {
            RunId = "run-alpha",
            StepId = "approval",
            SignalName = "approved",
        });
        var committedState = BuildState("running");
        committedState.ExecutionStates.Add("wait_signal", Any.Pack(waitState));

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = "workflow_execution_kernel",
                    State = Any.Pack(new WorkflowExecutionKernelState()),
                },
                committedState,
                version: 2,
                eventId: "evt-kernel-state",
                observedAt: "2026-06-24T13:00:05Z"));

        var document = store.Stored["root-actor"];
        document.CompletionStatus.Should().Be(WorkflowExecutionBoardCompletionStatus.WaitingForSignal);
        document.CurrentNodeId.Should().Be("approval");
        document.Summary.RunningNodes.Should().Be(0);
        document.Summary.WaitingOrPendingNodes.Should().Be(1);

        var node = document.NodeEntries.Should().ContainSingle().Subject;
        node.NodeId.Should().Be("approval");
        node.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Waiting);
        node.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:05Z"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeUnexecutedDownstreamStepsAsPendingWhenWorkflowWaits()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "collect_context",
                    StepType = "assign",
                },
                BuildState("running", workflowYaml: BoardPendingWorkflowYaml),
                version: 1,
                eventId: "evt-collect-request",
                observedAt: "2026-06-24T13:00:00Z"));
        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepCompletedEvent
                {
                    RunId = "run-alpha",
                    StepId = "collect_context",
                    Success = true,
                    NextStepId = "wait_for_signal",
                },
                BuildState("running", workflowYaml: BoardPendingWorkflowYaml),
                version: 2,
                eventId: "evt-collect-complete",
                observedAt: "2026-06-24T13:00:05Z"));
        var waitState = new WaitSignalModuleState();
        waitState.Pending.Add("run-alpha:approved:wait_for_signal", new PendingSignalState
        {
            RunId = "run-alpha",
            StepId = "wait_for_signal",
            SignalName = "approved",
        });
        var committedState = BuildState("running", workflowYaml: BoardPendingWorkflowYaml);
        committedState.ExecutionStates.Add("wait_signal", Any.Pack(waitState));

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = "wait_signal",
                    State = Any.Pack(waitState),
                },
                committedState,
                version: 3,
                eventId: "evt-wait-state",
                observedAt: "2026-06-24T13:00:10Z"));

        var document = store.Stored["root-actor"];
        document.Summary.CompletedSteps.Should().Be(1);
        document.Summary.RunningNodes.Should().Be(0);
        document.Summary.WaitingOrPendingNodes.Should().Be(3);
        document.Summary.FailedNodes.Should().Be(0);
        document.CurrentNodeId.Should().Be("wait_for_signal");
        document.NodeEntries.Should().HaveCount(4);

        document.NodeEntries.Single(node => node.NodeId == "collect_context")
            .Status.Should().Be(WorkflowExecutionBoardNodeStatus.Completed);
        document.NodeEntries.Single(node => node.NodeId == "wait_for_signal")
            .Status.Should().Be(WorkflowExecutionBoardNodeStatus.Waiting);
        document.NodeEntries.Single(node => node.NodeId == "summarize")
            .Status.Should().Be(WorkflowExecutionBoardNodeStatus.Pending);
        document.NodeEntries.Single(node => node.NodeId == "publish")
            .Status.Should().Be(WorkflowExecutionBoardNodeStatus.Pending);
    }

    [Fact]
    public async Task ProjectAsync_ShouldKeepStateRootWaitingNodeWhenPayloadMarksStepRunning()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        var waitState = new WaitSignalModuleState();
        waitState.Pending.Add("run-alpha:approved:approval", new PendingSignalState
        {
            RunId = "run-alpha",
            StepId = "approval",
            SignalName = "approved",
        });
        var committedState = BuildState("running");
        committedState.ExecutionStates.Add("wait_signal", Any.Pack(waitState));

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "approval",
                    StepType = "wait_signal",
                },
                committedState,
                version: 1,
                eventId: "evt-request-with-pending-root",
                observedAt: "2026-06-24T13:00:05Z"));

        var document = store.Stored["root-actor"];
        document.CompletionStatus.Should().Be(WorkflowExecutionBoardCompletionStatus.WaitingForSignal);
        document.CurrentNodeId.Should().Be("approval");
        document.Summary.RunningNodes.Should().Be(0);
        document.Summary.WaitingOrPendingNodes.Should().Be(1);

        var node = document.NodeEntries.Should().ContainSingle().Subject;
        node.NodeId.Should().Be("approval");
        node.Status.Should().Be(WorkflowExecutionBoardNodeStatus.Waiting);
        node.StepType.Should().Be("wait_signal");
        node.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:00:05Z"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldKeepDefinitionStepCountSeparateFromMaterializedNodes()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T13:00:00Z")));
        var context = CreateContext();

        var completedSteps = new[]
        {
            "save_input",
            "normalize_input",
            "save_normalized",
            "classify_request",
            "route_intent",
            "make_plan",
        };
        for (var i = 0; i < completedSteps.Length; i++)
        {
            var stepId = completedSteps[i];
            await materializer.ProjectAsync(
                context,
                WrapCommitted(
                    new StepCompletedEvent
                    {
                        RunId = "run-alpha",
                        StepId = stepId,
                        Success = true,
                    },
                    BuildState("running", workflowYaml: FifteenStepWorkflowYaml),
                    version: i + 1,
                    eventId: $"evt-completed-{i + 1}",
                    observedAt: $"2026-06-24T13:00:{i + 1:D2}Z"));
        }

        await materializer.ProjectAsync(
            context,
            WrapCommitted(
                new StepRequestEvent
                {
                    RunId = "run-alpha",
                    StepId = "polish_response",
                    StepType = "llm_call",
                },
                BuildState("running", workflowYaml: FifteenStepWorkflowYaml),
                version: 7,
                eventId: "evt-polish-running",
                observedAt: "2026-06-24T13:00:07Z"));

        var document = store.Stored["root-actor"];
        document.NodeEntries.Should().HaveCount(7);
        document.Summary.CompletedSteps.Should().Be(6);
        document.Summary.RunningNodes.Should().Be(1);
        document.Summary.WaitingOrPendingNodes.Should().Be(0);
        document.Summary.FailedNodes.Should().Be(0);
        document.Summary.DefinitionStepCount.Should().Be(15);
    }

    [Fact]
    public async Task ProjectAsync_ShouldSkipNonCommittedAndRelayedChildEnvelopes()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionBoardDocument>(x => x.Id);
        var materializer = new WorkflowExecutionBoardMaterializer(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-24T14:00:00Z")));

        await materializer.ProjectAsync(
            CreateContext(),
            new EventEnvelope
            {
                Id = "raw-envelope",
                Payload = Any.Pack(new WorkflowCompletedEvent()),
            });
        await materializer.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    RunId = "child-run",
                    Success = true,
                },
                BuildState("completed", runId: "child-run"),
                publisherActorId: "child-run"));

        store.Upserts.Should().BeEmpty();
    }

    private static StudioWorkflowBoardMaterializationContext CreateContext() =>
        new()
        {
            RootActorId = "root-actor",
            ProjectionKind = "workflow",
        };

    private static WorkflowRunState BuildState(
        string status,
        string runId = "run-alpha",
        string finalError = "",
        string workflowYaml = "") =>
        new()
        {
            LastCommandId = "cmd-alpha",
            DefinitionActorId = "definition-alpha",
            RunId = runId,
            WorkflowName = "workflow-alpha",
            ScopeId = "scope-alpha",
            Status = status,
            FinalError = finalError,
            WorkflowYaml = workflowYaml,
        };

    private const string BoardPendingWorkflowYaml = """
        name: board_pending_workflow
        roles: []
        steps:
          - id: collect_context
            type: assign
            next: wait_for_signal
            parameters:
              value: ready
          - id: wait_for_signal
            type: wait_signal
            next: summarize
            parameters:
              signal_name: approved
          - id: summarize
            type: assign
            next: publish
            parameters:
              value: summarized
          - id: publish
            type: assign
            parameters:
              value: published
        """;

    private const string FifteenStepWorkflowYaml = """
        name: fifteen_node_assistant_flow
        roles: []
        steps:
          - id: save_input
            type: assign
            next: normalize_input
          - id: normalize_input
            type: transform
            next: save_normalized
          - id: save_normalized
            type: assign
            next: classify_request
          - id: classify_request
            type: llm_call
            next: route_intent
          - id: route_intent
            type: switch
            branches:
              plan: make_plan
              _default: make_fallback
          - id: make_plan
            type: assign
            next: polish_response
          - id: polish_response
            type: llm_call
            next: finalize_plan
          - id: finalize_plan
            type: assign
          - id: make_fallback
            type: assign
            next: fallback_response
          - id: fallback_response
            type: llm_call
          - id: audit_input
            type: checkpoint
          - id: audit_route
            type: checkpoint
          - id: audit_generation
            type: checkpoint
          - id: archive_result
            type: assign
          - id: publish_summary
            type: assign
        """;

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        WorkflowRunState state,
        long version = 1,
        string eventId = "evt-1",
        string observedAt = "2026-06-24T13:00:00Z",
        string publisherActorId = "root-actor") =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(observedAt)),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingDocumentStore<TReadModel>(Func<TReadModel, string> keySelector)
        : IProjectionDocumentReader<TReadModel, string>,
          IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public Dictionary<string, TReadModel> Stored { get; } = new(StringComparer.Ordinal);
        public List<TReadModel> Upserts { get; } = [];

        public Task<TReadModel?> GetAsync(string id, CancellationToken ct = default)
        {
            Stored.TryGetValue(id, out var document);
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = Stored.Values.ToArray(),
            });

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            Stored[keySelector(readModel)] = readModel;
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            Stored.Remove(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
