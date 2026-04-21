using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionProjectorTests
{
    [Fact]
    public void TryUnpackRootStateEnvelope_ShouldReturnTypedState_AndRejectInvalidPayload()
    {
        var envelope = WrapCommitted(
            new WorkflowRunExecutionStartedEvent
            {
                WorkflowName = "wf-unpack",
                RunId = "run-unpack",
            },
            new WorkflowRunState
            {
                WorkflowName = "wf-unpack",
                RunId = "run-unpack",
                Status = "running",
            },
            version: 3,
            eventId: "evt-unpack");

        var ok = WorkflowExecutionArtifactMaterializationSupport.TryUnpackRootStateEnvelope(
            envelope,
            out var stateEvent,
            out var state);

        ok.Should().BeTrue();
        stateEvent.Should().NotBeNull();
        state.Should().NotBeNull();
        stateEvent!.EventId.Should().Be("evt-unpack");
        stateEvent.Version.Should().Be(3);
        state!.WorkflowName.Should().Be("wf-unpack");
        state.RunId.Should().Be("run-unpack");

        WorkflowExecutionArtifactMaterializationSupport.TryUnpackRootStateEnvelope(
                new EventEnvelope
                {
                    Id = "raw-envelope",
                    Payload = Any.Pack(new WorkflowRunExecutionStartedEvent()),
                },
                out stateEvent,
                out state)
            .Should()
            .BeFalse();
        stateEvent.Should().BeNull();
        state.Should().BeNull();
    }

    [Fact]
    public void ShouldSkip_ShouldRejectOlderAndDuplicateVersions()
    {
        var existing = new WorkflowRunInsightReportDocument
        {
            StateVersion = 5,
            LastEventId = "evt-5",
        };

        WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(
                existing,
                new StateEvent { Version = 4, EventId = "evt-4" })
            .Should()
            .BeTrue();
        WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(
                existing,
                new StateEvent { Version = 5, EventId = "evt-5" })
            .Should()
            .BeTrue();
        WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(
                existing,
                new StateEvent { Version = 5, EventId = "evt-6" })
            .Should()
            .BeFalse();
        WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(
                existing,
                new StateEvent { Version = 6, EventId = "evt-6" })
            .Should()
            .BeFalse();

        WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(
                new WorkflowRunInsightReportDocument
                {
                    StateVersion = 7,
                    LastEventId = string.Empty,
                },
                new StateEvent { Version = 7 })
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ApplyReportBase_ShouldPopulateLifecycleFieldsAndPreserveWaitingStatus()
    {
        var observedAt = new DateTimeOffset(2026, 3, 18, 3, 0, 0, TimeSpan.Zero);
        var context = CreateContext();

        var runningReport = new WorkflowRunInsightReportDocument
        {
            WorkflowName = "existing-name",
            CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal,
        };
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            runningReport,
            context,
            new WorkflowRunState
            {
                LastCommandId = "cmd-running",
                Status = "running",
            },
            new StateEvent
            {
                Version = 8,
                EventId = "evt-running",
            },
            observedAt);

        runningReport.RootActorId.Should().Be(context.RootActorId);
        runningReport.WorkflowName.Should().Be("existing-name");
        runningReport.CommandId.Should().Be("cmd-running");
        runningReport.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.WaitingForSignal);
        runningReport.Success.Should().BeNull();
        runningReport.CreatedAt.Should().Be(observedAt);
        runningReport.StartedAt.Should().Be(observedAt);
        runningReport.EndedAt.Should().Be(default(DateTimeOffset));

        var completedReport = new WorkflowRunInsightReportDocument
        {
            CreatedAt = observedAt.AddMinutes(-10),
        };
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            completedReport,
            context,
            new WorkflowRunState
            {
                WorkflowName = "wf-completed",
                Status = "completed",
            },
            new StateEvent
            {
                Version = 9,
                EventId = "evt-completed",
            },
            observedAt);

        completedReport.WorkflowName.Should().Be("wf-completed");
        completedReport.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);
        completedReport.Success.Should().BeTrue();
        completedReport.CreatedAt.Should().Be(observedAt.AddMinutes(-10));
        completedReport.EndedAt.Should().Be(observedAt);
    }

    [Fact]
    public void CreateReportDocument_ShouldInitializeDefaultsForUnknownStatus()
    {
        var observedAt = new DateTimeOffset(2026, 3, 18, 3, 30, 0, TimeSpan.Zero);

        var report = WorkflowExecutionArtifactMaterializationSupport.CreateReportDocument(
            CreateContext(),
            new WorkflowRunState
            {
                Status = "mystery",
            },
            new StateEvent
            {
                Version = 2,
                EventId = "evt-create",
            },
            observedAt);

        report.ReportVersion.Should().Be("3.0");
        report.ProjectionScope.Should().Be(WorkflowExecutionProjectionScope.RunIsolated);
        report.TopologySource.Should().Be(WorkflowExecutionTopologySource.RuntimeSnapshot);
        report.WorkflowName.Should().BeEmpty();
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Unknown);
        report.Success.Should().BeNull();
        report.CreatedAt.Should().Be(observedAt);
        report.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public void ApplyReportBase_ShouldResolveFailedAndStoppedStatuses()
    {
        var observedAt = new DateTimeOffset(2026, 3, 18, 3, 45, 0, TimeSpan.Zero);
        var context = CreateContext();

        var failedReport = new WorkflowRunInsightReportDocument();
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            failedReport,
            context,
            new WorkflowRunState
            {
                WorkflowName = "wf-failed",
                Status = "failed",
                FinalError = "boom",
            },
            new StateEvent
            {
                Version = 3,
                EventId = "evt-failed",
            },
            observedAt);

        var stoppedReport = new WorkflowRunInsightReportDocument
        {
            WorkflowName = "existing-name",
        };
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            stoppedReport,
            context,
            new WorkflowRunState
            {
                Status = "stopped",
            },
            new StateEvent
            {
                Version = 4,
                EventId = "evt-stopped",
            },
            observedAt);

        failedReport.WorkflowName.Should().Be("wf-failed");
        failedReport.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Failed);
        failedReport.Success.Should().BeFalse();
        failedReport.EndedAt.Should().Be(observedAt);

        stoppedReport.WorkflowName.Should().Be("existing-name");
        stoppedReport.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        stoppedReport.Success.Should().BeFalse();
        stoppedReport.EndedAt.Should().Be(observedAt);
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldTrackObservedWorkflowArtifactsAcrossBranches()
    {
        var context = CreateContext();
        var baselineTimestamp = new DateTimeOffset(2026, 3, 18, 4, 0, 0, TimeSpan.Zero);
        var report = WorkflowExecutionArtifactMaterializationSupport.CreateReportDocument(
            context,
            new WorkflowRunState
            {
                WorkflowName = "wf-base",
                LastCommandId = "cmd-1",
                Status = "running",
            },
            new StateEvent
            {
                Version = 1,
                EventId = "evt-base",
            },
            baselineTimestamp);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            new StateEvent
            {
                Version = 2,
                EventId = "evt-null",
            },
            baselineTimestamp.AddSeconds(1));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRunExecutionStartedEvent
                {
                    WorkflowName = string.Empty,
                    Input = "hello world",
                },
                3,
                "evt-start"),
            baselineTimestamp.AddSeconds(2));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    Parameters =
                    {
                        ["temperature"] = "0.2",
                        ["max_tokens"] = "128",
                    },
                },
                4,
                "evt-step-request"),
            baselineTimestamp.AddSeconds(3));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "step-1",
                    Success = false,
                    Output = new string('x', 260),
                    Error = "tool failed",
                    WorkerId = "worker-1",
                    NextStepId = "step-2",
                    BranchKey = "fallback",
                    AssignedVariable = "answer",
                    AssignedValue = "42",
                    Annotations =
                    {
                        ["reason"] = "timeout",
                    },
                },
                5,
                "evt-step-completed"),
            baselineTimestamp.AddSeconds(4));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "step-1",
                    SuspensionType = "human_input",
                    Prompt = "Need approval",
                    VariableName = "approval",
                    Metadata =
                    {
                        ["channel"] = "ui",
                    },
                },
                6,
                "evt-suspended"),
            baselineTimestamp.AddSeconds(5));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WaitingForSignalEvent
                {
                    StepId = "step-1",
                    SignalName = "continue",
                    TimeoutMs = 900,
                },
                7,
                "evt-waiting"),
            baselineTimestamp.AddSeconds(6));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowSignalBufferedEvent
                {
                    StepId = "step-1",
                    SignalName = "continue",
                },
                8,
                "evt-buffered"),
            baselineTimestamp.AddSeconds(7));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRoleActorLinkedEvent
                {
                    ChildActorId = "role-actor-1",
                },
                9,
                "evt-role-link"),
            baselineTimestamp.AddSeconds(8));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new SubWorkflowBindingUpsertedEvent
                {
                    ChildActorId = "role-actor-1",
                },
                10,
                "evt-subworkflow-duplicate"),
            baselineTimestamp.AddSeconds(9));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new SubWorkflowBindingUpsertedEvent
                {
                    ChildActorId = "child-run-1",
                },
                11,
                "evt-subworkflow-link"),
            baselineTimestamp.AddSeconds(10));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new SubWorkflowBindingUpsertedEvent(),
                12,
                "evt-subworkflow-blank"),
            baselineTimestamp.AddSeconds(11));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRoleReplyRecordedEvent
                {
                    RoleActorId = "role-actor-1",
                    SessionId = "session-1",
                    Content = "response",
                    ToolCalls =
                    {
                        new WorkflowRoleReplyToolCall
                        {
                            ToolName = "search",
                            CallId = "call-1",
                        },
                        new WorkflowRoleReplyToolCall
                        {
                            ToolName = "fetch",
                            CallId = "call-2",
                        },
                    },
                },
                13,
                "evt-role-reply"),
            baselineTimestamp.AddSeconds(12));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowCompletedEvent
                {
                    Success = false,
                    Error = "failed-hard",
                },
                14,
                "evt-workflow-failed"),
            baselineTimestamp.AddSeconds(13));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRunStoppedEvent(),
                15,
                "evt-workflow-stopped"),
            baselineTimestamp.AddSeconds(14));

        report.Steps.Should().ContainSingle();
        var step = report.Steps.Single();
        step.StepId.Should().Be("step-1");
        step.StepType.Should().Be("llm_call");
        step.TargetRole.Should().Be("assistant");
        step.RequestedAt.Should().Be(baselineTimestamp.AddSeconds(3));
        step.CompletedAt.Should().Be(baselineTimestamp.AddSeconds(4));
        step.Success.Should().BeFalse();
        step.WorkerId.Should().Be("worker-1");
        step.OutputPreview.Should().EndWith("...");
        step.OutputPreview.Length.Should().Be(243);
        step.Error.Should().Be("tool failed");
        step.NextStepId.Should().Be("step-2");
        step.BranchKey.Should().Be("fallback");
        step.AssignedVariable.Should().Be("answer");
        step.AssignedValue.Should().Be("42");
        step.SuspensionType.Should().Be("human_input");
        step.SuspensionPrompt.Should().Be("Need approval");
        step.SuspensionTimeoutSeconds.Should().BeNull();
        step.RequestedVariableName.Should().Be("approval");
        step.RequestParameters.Should().Contain(new KeyValuePair<string, string>("temperature", "0.2"));
        step.CompletionAnnotations.Should().Contain(new KeyValuePair<string, string>("reason", "timeout"));

        report.Topology.Should().HaveCount(2);
        report.Topology.Should().Contain(x => x.Parent == "root-actor" && x.Child == "role-actor-1");
        report.Topology.Should().Contain(x => x.Parent == "root-actor" && x.Child == "child-run-1");

        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("role-actor-1");
        report.RoleReplies[0].SessionId.Should().Be("session-1");
        report.RoleReplies[0].ContentLength.Should().Be(8);

        report.Timeline.Should().Contain(x => x.Stage == "workflow.start" && x.Message == "command=cmd-1");
        report.Timeline.Should().Contain(x => x.Stage == "step.request" && x.StepId == "step-1");
        report.Timeline.Should().Contain(x => x.Stage == "step.failed" && x.StepId == "step-1");
        report.Timeline.Should().Contain(x => x.Stage == "workflow.suspended" && x.StepId == "step-1");
        report.Timeline.Should().Contain(x => x.Stage == "signal.waiting" && x.Data["timeout_ms"] == "900");
        report.Timeline.Should().Contain(x => x.Stage == "signal.buffered");
        report.Timeline.Count(x => x.Stage == "tool.call").Should().Be(2);
        report.Timeline.Should().Contain(x => x.Stage == "workflow.failed");
        report.Timeline.Should().Contain(x => x.Stage == "workflow.stopped" && x.Message == string.Empty);

        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.Success.Should().BeFalse();
        report.FinalOutput.Should().BeEmpty();
        report.FinalError.Should().Be("failed-hard");
        report.EndedAt.Should().Be(baselineTimestamp.AddSeconds(14));
        report.Summary.TotalSteps.Should().Be(1);
        report.Summary.RequestedSteps.Should().Be(1);
        report.Summary.CompletedSteps.Should().Be(1);
        report.Summary.RoleReplyCount.Should().Be(1);
        report.Summary.StepTypeCounts.Should().Contain(new KeyValuePair<string, int>("llm_call", 1));
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldCaptureSuccessfulLifecycleStages()
    {
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-2",
        };
        var timestamp = new DateTimeOffset(2026, 3, 18, 5, 0, 0, TimeSpan.Zero);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "step-ok",
                    Success = true,
                    Output = "done",
                },
                20,
                "evt-step-ok"),
            timestamp);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowCompletedEvent
                {
                    Success = true,
                    Output = "workflow done",
                },
                21,
                "evt-workflow-ok"),
            timestamp.AddSeconds(1));

        report.Timeline.Should().Contain(x => x.Stage == "step.completed");
        report.Timeline.Should().Contain(x => x.Stage == "workflow.completed");
        report.Success.Should().BeTrue();
        report.FinalOutput.Should().Be("workflow done");
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldHandleWorkflowStoppedEvent()
    {
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-stop",
            FinalOutput = "previous",
            FinalError = string.Empty,
            Success = null,
        };
        var timestamp = new DateTimeOffset(2026, 3, 18, 6, 0, 0, TimeSpan.Zero);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowStoppedEvent
                {
                    WorkflowName = "review",
                    RunId = "run-stop",
                    Reason = "manual",
                },
                21,
                "evt-workflow-stopped-domain"),
            timestamp);

        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.Success.Should().BeFalse();
        report.FinalOutput.Should().BeEmpty();
        report.FinalError.Should().Be("manual");
        report.EndedAt.Should().Be(timestamp);
        report.Timeline.Should().Contain(x => x.Stage == "workflow.stopped" && x.Message == "manual");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldRespectExplicitRoleIds_AndPreserveStartedAt()
    {
        var startedAt = new DateTimeOffset(2026, 3, 18, 5, 30, 0, TimeSpan.Zero);
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-explicit-role",
            StartedAt = startedAt,
        };

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRunExecutionStartedEvent
                {
                    WorkflowName = "wf-explicit-role",
                    Input = "payload",
                },
                22,
                "evt-start-explicit-role"),
            startedAt.AddSeconds(1));
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRoleReplyRecordedEvent
                {
                    RoleId = "assistant",
                    RoleActorId = "root-actor:assistant",
                    SessionId = "session-explicit",
                    Content = "ok",
                },
                23,
                "evt-role-explicit"),
            startedAt.AddSeconds(2));

        report.StartedAt.Should().Be(startedAt);
        report.WorkflowName.Should().Be("wf-explicit-role");
        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("assistant");
        report.RoleReplies[0].ContentLength.Should().Be(2);
        report.Timeline.Should().ContainSingle(x =>
            x.Stage == "role.reply" &&
            x.Message == "assistant" &&
            x.Data["session_id"] == "session-explicit");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldHandleSuccessfulSteps_SuspensionTimeouts_AndStoppedReasons()
    {
        var timestamp = new DateTimeOffset(2026, 3, 18, 5, 45, 0, TimeSpan.Zero);
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-step-stop",
        };

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "step-2",
                    StepType = "emit",
                },
                24,
                "evt-step-2-request"),
            timestamp);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "step-2",
                    Success = true,
                    Output = "done",
                },
                25,
                "evt-step-2-complete"),
            timestamp.AddSeconds(1));
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "step-2",
                    SuspensionType = "approval",
                    Prompt = "approve",
                    TimeoutSeconds = 60,
                    VariableName = "approved",
                },
                26,
                "evt-step-2-suspend"),
            timestamp.AddSeconds(2));
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowRunStoppedEvent
                {
                    Reason = "manual",
                },
                27,
                "evt-step-2-stopped"),
            timestamp.AddSeconds(3));

        report.Steps.Should().ContainSingle();
        report.Steps[0].SuspensionTimeoutSeconds.Should().Be(60);
        report.Steps[0].RequestedVariableName.Should().Be("approved");
        report.Timeline.Should().Contain(x => x.Stage == "step.completed" && x.Message == "step-2 (success)");
        report.Timeline.Should().Contain(x => x.Stage == "workflow.stopped" && x.Message == "manual");
        report.FinalError.Should().Be("manual");
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
    }

    [Fact]
    public void BuildTimelineAndGraphDocuments_ShouldCloneCollections()
    {
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-3",
            WorkflowName = "wf-clone",
            Input = "payload",
            StateVersion = 22,
            LastEventId = "evt-22",
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 6, 0, 0, TimeSpan.Zero),
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 3, 18, 6, 0, 1, TimeSpan.Zero),
                    Stage = "step.request",
                    Data = { ["key"] = "value" },
                },
            ],
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    RequestParameters = { ["temperature"] = "0.2" },
                },
            ],
            Topology =
            [
                new WorkflowExecutionTopologyEdge("root-actor", "child-1"),
            ],
        };

        var timelineDocument = WorkflowExecutionArtifactMaterializationSupport.BuildTimelineDocument(report);
        var graphDocument = WorkflowExecutionArtifactMaterializationSupport.BuildGraphDocument(report);

        timelineDocument.Timeline[0].Data["key"] = "changed";
        graphDocument.Steps[0].RequestParameters["temperature"] = "0.9";
        graphDocument.Topology.Add(new WorkflowExecutionTopologyEdge("root-actor", "child-2"));

        report.Timeline[0].Data["key"].Should().Be("value");
        report.Steps[0].RequestParameters["temperature"].Should().Be("0.2");
        report.Topology.Should().ContainSingle();
        timelineDocument.RootActorId.Should().Be("root-actor");
        graphDocument.WorkflowName.Should().Be("wf-clone");
    }

    [Theory]
    [MemberData(nameof(CurrentStateStatusCases))]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldMapCommittedStateSnapshots(
        string status,
        bool? expectedSuccess)
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 18, 7, 0, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    Success = true,
                },
                new WorkflowRunState
                {
                    LastCommandId = "cmd-current",
                    DefinitionActorId = "definition-1",
                    WorkflowName = "wf-current",
                    Status = status,
                    Compiled = true,
                    CompilationError = "none",
                    Input = "hello",
                    FinalOutput = "done",
                    FinalError = "err",
                },
                includeEnvelopeTimestamp: false));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.RootActorId.Should().Be("root-actor");
        document.RunId.Should().Be("root-actor");
        document.CommandId.Should().Be("cmd-current");
        document.DefinitionActorId.Should().Be("definition-1");
        document.WorkflowName.Should().Be("wf-current");
        document.Status.Should().Be(status);
        document.Compiled.Should().BeTrue();
        document.ExecutionStateCount.Should().Be(0);
        document.Success.Should().Be(expectedSuccess);
        document.UpdatedAt.Should().Be(new DateTimeOffset(2026, 3, 18, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_WhenEnvelopeIsNotCommittedState_ShouldSkipWrite()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 18, 7, 30, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            new EventEnvelope
            {
                Id = "raw-envelope",
                Payload = Any.Pack(new WorkflowCompletedEvent()),
            });

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowRunGraphArtifactMaterializer_ShouldNormalizeTokensAndDeduplicateNodesAndEdges()
    {
        var readModel = new WorkflowRunGraphArtifactDocument
        {
            RootActorId = " ",
            CommandId = " ",
            WorkflowName = "wf-graph",
            Input = "payload",
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = " ",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    WorkerId = "worker-1",
                    Success = true,
                },
            ],
            Topology =
            [
                new WorkflowExecutionTopologyEdge(" ", "child-1"),
                new WorkflowExecutionTopologyEdge("unknown", "child-1"),
            ],
        };

        var materialization = new WorkflowRunGraphArtifactMaterializer().Materialize(readModel);

        materialization.Scope.Should().Be(WorkflowExecutionGraphConstants.Scope);
        materialization.Nodes.Should().Contain(x => x.NodeId == "unknown" && x.NodeType == WorkflowExecutionGraphConstants.ActorNodeType);
        materialization.Nodes.Should().Contain(x => x.NodeId == "run:unknown:unknown" && x.NodeType == WorkflowExecutionGraphConstants.RunNodeType);
        materialization.Nodes.Should().Contain(x => x.NodeId == "step:unknown:unknown:unknown" && x.NodeType == WorkflowExecutionGraphConstants.StepNodeType);
        materialization.Nodes.Should().Contain(x => x.NodeId == "child-1");

        materialization.Edges.Should().Contain(x =>
            x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeOwns &&
            x.FromNodeId == "unknown" &&
            x.ToNodeId == "run:unknown:unknown");
        materialization.Edges.Should().Contain(x =>
            x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeContainsStep &&
            x.ToNodeId == "step:unknown:unknown:unknown");
        materialization.Edges.Count(x =>
                x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf &&
                x.FromNodeId == "unknown" &&
                x.ToNodeId == "child-1")
            .Should()
            .Be(1);
    }

    [Fact]
    public void WorkflowExecutionReadModelMapper_ShouldMapReportAndGraphData()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var currentState = new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-1",
            WorkflowName = string.Empty,
            CommandId = "cmd-4",
            Status = "running",
            FinalOutput = string.Empty,
            FinalError = string.Empty,
            StateVersion = 30,
            LastEventId = "evt-30",
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
        };
        var report = new WorkflowRunInsightReportDocument
        {
            WorkflowName = "wf-report",
            CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal,
            Success = true,
            FinalOutput = "done",
            FinalError = "ignored",
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 3,
                RequestedSteps = 2,
                CompletedSteps = 1,
                RoleReplyCount = 4,
            },
        };

        var snapshot = mapper.ToActorSnapshot(currentState, report);
        var unknownSnapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-2",
            Status = "mystery",
        });
        var timelineItem = mapper.ToActorTimelineItem(new WorkflowExecutionTimelineEvent
        {
            Timestamp = new DateTimeOffset(2026, 3, 18, 8, 1, 0, TimeSpan.Zero),
            Stage = "signal.waiting",
            Data = { ["signal_name"] = "continue" },
        });
        var node = mapper.ToActorGraphNode(new ProjectionGraphNode
        {
            NodeId = "node-1",
            NodeType = "Actor",
            Properties = { ["key"] = "value" },
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 8, 2, 0, TimeSpan.Zero),
        });
        var edge = mapper.ToActorGraphEdge(new ProjectionGraphEdge
        {
            EdgeId = "edge-1",
            FromNodeId = "node-1",
            ToNodeId = "node-2",
            EdgeType = "CHILD_OF",
            Properties = { ["kind"] = "runtime" },
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 8, 3, 0, TimeSpan.Zero),
        });
        var subgraph = mapper.ToActorGraphSubgraph(
            "node-1",
            new ProjectionGraphSubgraph
            {
                Nodes =
                [
                    new ProjectionGraphNode
                    {
                        NodeId = "node-1",
                        NodeType = "Actor",
                    },
                ],
                Edges =
                [
                    new ProjectionGraphEdge
                    {
                        EdgeId = "edge-1",
                        FromNodeId = "node-1",
                        ToNodeId = "node-2",
                    },
                ],
            });

        snapshot.ActorId.Should().Be("actor-1");
        snapshot.WorkflowName.Should().Be("wf-report");
        snapshot.CompletionStatus.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunCompletionStatus.Running);
        snapshot.LastSuccess.Should().BeTrue();
        snapshot.LastOutput.Should().Be("done");
        snapshot.TotalSteps.Should().Be(3);
        snapshot.RoleReplyCount.Should().Be(4);

        unknownSnapshot.CompletionStatus.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunCompletionStatus.Unknown);
        timelineItem.Data.Should().Contain(new KeyValuePair<string, string>("signal_name", "continue"));
        node.Properties.Should().Contain(new KeyValuePair<string, string>("key", "value"));
        edge.Properties.Should().Contain(new KeyValuePair<string, string>("kind", "runtime"));
        subgraph.RootNodeId.Should().Be("node-1");
        subgraph.Nodes.Should().ContainSingle();
        subgraph.Edges.Should().ContainSingle();
    }

    public static IEnumerable<object?[]> CurrentStateStatusCases()
    {
        yield return ["completed", true];
        yield return ["failed", false];
        yield return ["running", null];
        yield return ["unknown", null];
    }

    private static WorkflowExecutionMaterializationContext CreateContext() =>
        new()
        {
            RootActorId = "root-actor",
            ProjectionKind = "workflow",
        };

    private static StateEvent PackStateEvent(
        IMessage payload,
        long version,
        string eventId)
    {
        return new StateEvent
        {
            Version = version,
            EventId = eventId,
            EventData = Any.Pack(payload),
        };
    }

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        WorkflowRunState state,
        long version = 1,
        string eventId = "evt-1",
        bool includeEnvelopeTimestamp = true)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = includeEnvelopeTimestamp
                ? Timestamp.FromDateTime(DateTime.UtcNow)
                : null,
            Route = EnvelopeRouteSemantics.CreateObserverPublication("root-actor"),
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
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
