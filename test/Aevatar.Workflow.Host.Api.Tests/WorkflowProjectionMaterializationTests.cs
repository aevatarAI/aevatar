using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionMaterializationTests
{
    [Fact]
    public void WorkflowRunInsightReportArtifactProjector_Ctor_ShouldThrow_WhenDependencyMissing()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var timelineStore = new RecordingDocumentStore<WorkflowRunTimelineDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);

        Action noReader = () => new WorkflowRunInsightReportArtifactProjector(null!, reportStore, timelineStore, graphWriter);
        Action noReportWriter = () => new WorkflowRunInsightReportArtifactProjector(reportStore, null!, timelineStore, graphWriter);
        Action noTimelineWriter = () => new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, null!, graphWriter);
        Action noGraphWriter = () => new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, timelineStore, null!);

        noReader.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("reportReader");
        noReportWriter.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("reportWriter");
        noTimelineWriter.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("timelineWriter");
        noGraphWriter.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("graphWriter");
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldWriteCommittedSnapshot_AndIgnoreInvalidEnvelope()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionCurrentStateDocument>(x => x.Id);
        var projector = new WorkflowExecutionCurrentStateProjector(store, new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = false,
                    Output = "partial",
                    Error = "boom",
                    RunId = "run-1",
                },
                BuildState("failed", finalError: "boom")));

        store.UpsertCount.Should().Be(1);
        store.Stored.Should().ContainKey("actor-1");
        store.Stored["actor-1"].Success.Should().BeFalse();
        store.Stored["actor-1"].RunId.Should().Be("run-1");
        store.Stored["actor-1"].Status.Should().Be("failed");
        store.Stored["actor-1"].FinalError.Should().Be("boom");

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = true,
                    Output = "done",
                    RunId = "run-1",
                },
                BuildState("completed", finalOutput: "done")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRunStoppedEvent
                {
                    RunId = "run-1",
                    Reason = "manual-stop",
                },
                BuildState("stopped", runId: "", finalError: "manual-stop")));

        store.UpsertCount.Should().Be(3);
        store.Stored.Should().ContainKey("actor-1");
        store.Stored["actor-1"].Success.Should().BeNull();
        store.Stored["actor-1"].RunId.Should().Be("actor-1");
        store.Stored["actor-1"].Status.Should().Be("stopped");
        store.Stored["actor-1"].FinalError.Should().Be("manual-stop");
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldTrackLifecycleReplyAndCompletionBranches()
    {
        var store = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var timelineStore = new RecordingDocumentStore<WorkflowRunTimelineDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(store, store, timelineStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                1,
                new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-1",
                    WorkflowName = "wf-1",
                    Input = "hello",
                    DefinitionActorId = "definition-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    Parameters = { ["temperature"] = "0.2" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = false,
                    Output = "partial",
                    Error = "boom",
                    WorkerId = "role-1",
                    NextStepId = "step-2",
                    BranchKey = "fallback",
                    AssignedVariable = "answer",
                    AssignedValue = "42",
                    Annotations = { ["token_usage"] = "99" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRoleReplyRecordedEvent
                {
                    RunId = "run-1",
                    RoleActorId = "role-1",
                    RoleId = "",
                    SessionId = "session-1",
                    Content = "tool says hi",
                    ToolCalls =
                    {
                        new WorkflowRoleReplyToolCall
                        {
                            ToolName = "search",
                            CallId = "call-1",
                        },
                    },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = true,
                    Output = "done",
                    RunId = "run-1",
                },
                BuildState("completed", finalOutput: "done")));

        var report = store.Stored["actor-1"];
        report.WorkflowName.Should().Be("wf-1");
        report.Input.Should().Be("hello");
        report.Success.Should().BeTrue();
        report.FinalOutput.Should().Be("done");
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);
        report.Steps.Should().ContainSingle();
        report.Steps[0].StepId.Should().Be("step-1");
        report.Steps[0].Success.Should().BeFalse();
        report.Steps[0].OutputPreview.Should().Be("partial");
        report.Steps[0].CompletionAnnotations.Should().ContainKey("token_usage");
        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("role-1");
        report.Timeline.Select(x => x.Stage).Should().Contain([
            "workflow.start",
            "step.request",
            "step.failed",
            "role.reply",
            "tool.call",
            "workflow.completed",
        ]);
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldTrackSuspensionSignalAndStoppedBranches()
    {
        var store = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var timelineStore = new RecordingDocumentStore<WorkflowRunTimelineDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(store, store, timelineStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                1,
                new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SuspensionType = "wait_signal",
                    Prompt = "Need approval",
                    TimeoutSeconds = 30,
                    VariableName = "approval",
                    Metadata = { ["source"] = "user" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new WaitingForSignalEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SignalName = "approve",
                    TimeoutMs = 120000,
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowSignalBufferedEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SignalName = "approve",
                    Payload = "{}",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRunStoppedEvent
                {
                    RunId = "run-1",
                    Reason = "manual-stop",
                },
                BuildState("stopped", finalError: "manual-stop")));

        var report = store.Stored["actor-1"];
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.FinalError.Should().Be("manual-stop");
        report.Steps.Should().ContainSingle();
        report.Steps[0].SuspensionType.Should().Be("wait_signal");
        report.Steps[0].SuspensionPrompt.Should().Be("Need approval");
        report.Steps[0].SuspensionTimeoutSeconds.Should().Be(30);
        report.Timeline.Select(x => x.Stage).Should().Contain([
            "workflow.suspended",
            "signal.waiting",
            "signal.buffered",
            "workflow.stopped",
        ]);
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldIgnoreInvalidEnvelope_AndMissingStateRoot()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var timelineStore = new RecordingDocumentStore<WorkflowRunTimelineDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, timelineStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing-state",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-17T11:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing-state",
                        Version = 6,
                        EventData = Any.Pack(new WorkflowCompletedEvent
                        {
                            WorkflowName = "wf-1",
                            RunId = "run-1",
                            Success = true,
                            Output = "done",
                        }),
                    },
                }),
            });

        reportStore.UpsertCount.Should().Be(0);
        timelineStore.UpsertCount.Should().Be(0);
        graphWriter.UpsertCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowArtifactProjector_ShouldTrackStepAndTopologyEvents_AndSkipDuplicates()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var timelineStore = new RecordingDocumentStore<WorkflowRunTimelineDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, timelineStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };
        var requestEnvelope = BuildCommittedEnvelope(
            1,
            new StepRequestEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                StepType = "tool_call",
                TargetRole = "assistant",
                Parameters = { ["query"] = "weather" },
            },
            BuildState("running"));

        await projector.ProjectAsync(context, requestEnvelope);
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                    Output = "sunny",
                    WorkerId = "role-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    SuspensionType = "human_input",
                    Prompt = "confirm",
                    TimeoutSeconds = 15,
                    VariableName = "answer",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRoleActorLinkedEvent
                {
                    RunId = "run-1",
                    RoleId = "assistant",
                    ChildActorId = "role-actor-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new SubWorkflowBindingUpsertedEvent
                {
                    WorkflowName = "sub-flow",
                    ChildActorId = "child-run-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new SubWorkflowBindingUpsertedEvent
                {
                    WorkflowName = "sub-flow",
                    ChildActorId = "child-run-1",
                },
                BuildState("running"),
                eventId: "evt-5"));

        reportStore.UpsertCount.Should().Be(5);
        timelineStore.UpsertCount.Should().Be(5);
        graphWriter.UpsertCount.Should().Be(5);
        timelineStore.Stored["actor-1"].Timeline.Select(x => x.Stage).Should().Contain(["step.request", "step.completed"]);
        graphWriter.Stored["actor-1"].Steps.Should().ContainSingle();
        graphWriter.Stored["actor-1"].Steps[0].TargetRole.Should().Be("assistant");
        graphWriter.Stored["actor-1"].Steps[0].SuspensionType.Should().Be("human_input");
        graphWriter.Stored["actor-1"].Topology.Select(x => x.Child).Should().Contain(["role-actor-1", "child-run-1"]);
        reportStore.Stored["actor-1"].Timeline.Select(x => x.Stage).Should().Contain(["step.request", "step.completed", "workflow.suspended"]);
    }

    [Fact]
    public async Task WorkflowExecutionMaterializationPort_And_Codecs_ShouldCoverLifecycleBranches()
    {
        var activation = new RecordingMaterializationActivationService();
        var release = new RecordingMaterializationReleaseService();
        var port = new WorkflowExecutionMaterializationPort(
            new Aevatar.Workflow.Projection.Configuration.WorkflowExecutionProjectionOptions { Enabled = true },
            activation,
            release);

        (await port.ActivateAsync("")).Should().BeFalse();
        (await port.ActivateAsync("actor-2")).Should().BeTrue();

        activation.Requests.Should().ContainSingle();
        activation.Requests[0].ProjectionKind.Should().Be("workflow-execution-materialization");

        var materializationLease = new WorkflowExecutionMaterializationRuntimeLease(new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-execution-materialization",
        });
        var bindingLease = new WorkflowBindingRuntimeLease(new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-binding",
        });
        materializationLease.Context.RootActorId.Should().Be("actor-2");
        bindingLease.Context.ProjectionKind.Should().Be("workflow-binding");

        var bindingCodec = new WorkflowBindingSessionEventCodec();
        bindingCodec.Channel.Should().Be("workflow-binding");
        var bindingEnvelope = new EventEnvelope
        {
            Id = "binding-1",
            Payload = Any.Pack(new StringValue { Value = "binding" }),
        };
        var payload = bindingCodec.Serialize(bindingEnvelope);
        bindingCodec.Deserialize(bindingCodec.GetEventType(bindingEnvelope), payload)!.Id.Should().Be("binding-1");
        bindingCodec.Deserialize("mismatch", payload).Should().BeNull();

        var runCodec = new WorkflowRunEventSessionCodec();
        var runEnvelope = new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload { Name = "evt" },
        };
        runCodec.Deserialize(runCodec.GetEventType(runEnvelope), runCodec.Serialize(runEnvelope))!.Custom.Name.Should().Be("evt");
        runCodec.Deserialize(string.Empty, ByteString.Empty).Should().BeNull();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        long version,
        IMessage payload,
        WorkflowRunState state,
        string? eventId = null)
    {
        var timestamp = DateTimeOffset.Parse($"2026-03-17T10:{version:00}:00+00:00");
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId ?? $"evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static WorkflowRunState BuildState(
        string status,
        string runId = "run-1",
        string finalOutput = "",
        string finalError = "") =>
        new()
        {
            RunId = runId,
            WorkflowName = "wf-1",
            LastCommandId = "cmd-1",
            DefinitionActorId = "definition-1",
            Status = status,
            Input = "hello",
            FinalOutput = finalOutput,
            FinalError = finalError,
            Compiled = true,
        };

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingDocumentStore<TReadModel>
        : IProjectionDocumentReader<TReadModel, string>,
          IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly Func<TReadModel, string> _keySelector;

        public RecordingDocumentStore(Func<TReadModel, string> keySelector)
        {
            _keySelector = keySelector;
        }

        public Dictionary<string, TReadModel> Stored { get; } = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            Stored[_keySelector(readModel)] = readModel;
            UpsertCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            Stored.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingGraphWriter<TReadModel> : IProjectionGraphWriter<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly Func<TReadModel, string> _keySelector;

        public RecordingGraphWriter(Func<TReadModel, string> keySelector)
        {
            _keySelector = keySelector;
        }

        public Dictionary<string, TReadModel> Stored { get; } = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stored[_keySelector(readModel)] = readModel;
            UpsertCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMaterializationActivationService
        : IProjectionMaterializationActivationService<WorkflowExecutionMaterializationRuntimeLease>
    {
        public List<ProjectionMaterializationStartRequest> Requests { get; } = [];

        public Task<WorkflowExecutionMaterializationRuntimeLease> EnsureAsync(
            ProjectionMaterializationStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WorkflowExecutionMaterializationRuntimeLease(new WorkflowExecutionMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        }
    }

    private sealed class RecordingMaterializationReleaseService
        : IProjectionMaterializationReleaseService<WorkflowExecutionMaterializationRuntimeLease>
    {
        public Task ReleaseIfIdleAsync(WorkflowExecutionMaterializationRuntimeLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
