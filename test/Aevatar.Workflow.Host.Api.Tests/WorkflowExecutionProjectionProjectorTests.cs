using System.Diagnostics.Metrics;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Security;
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
        report.TopologySource.Should().Be(WorkflowExecutionTopologySource.CommittedProjection);
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
                    Secure = true,
                    RedactedOutput = "[captured]",
                    Metadata =
                    {
                        ["channel"] = "ui",
                        ["variable"] = "legacy_approval",
                        ["secure"] = "false",
                        ["input_mode"] = "password",
                        ["redacted_output"] = "[legacy]",
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
        var suspendedTimeline = report.Timeline.Single(x => x.Stage == "workflow.suspended" && x.StepId == "step-1");
        suspendedTimeline.Data.Should().ContainKey("channel").WhoseValue.Should().Be("ui");
        suspendedTimeline.Data.Should().ContainKey("variable").WhoseValue.Should().Be("approval");
        suspendedTimeline.Data.Should().ContainKey("secure").WhoseValue.Should().Be("true");
        suspendedTimeline.Data.Should().ContainKey("redacted_output").WhoseValue.Should().Be("[captured]");
        suspendedTimeline.Data.Should().NotContainKey("input_mode");
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
    public void ApplyObservedPayloadToReport_ShouldIgnoreLegacyOnlySecureInputMetadataReservedKeys()
    {
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-legacy-secure",
        };
        var timestamp = new DateTimeOffset(2026, 3, 18, 5, 10, 0, TimeSpan.Zero);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "secure-input",
                    SuspensionType = "secure_input",
                    Prompt = "enter secret",
                    Metadata =
                    {
                        ["variable"] = "api_key",
                        ["secure"] = "true",
                        ["input_mode"] = "password",
                        ["redacted_output"] = "[legacy captured]",
                        ["source"] = "legacy-test",
                    },
                },
                30,
                "evt-legacy-secure"),
            timestamp);

        var suspendedTimeline = report.Timeline.Single(x =>
            x.Stage == "workflow.suspended" &&
            x.StepId == "secure-input");
        suspendedTimeline.Data.Should().ContainKey("source").WhoseValue.Should().Be("legacy-test");
        suspendedTimeline.Data.Should().NotContainKey("variable");
        suspendedTimeline.Data.Should().NotContainKey("secure");
        suspendedTimeline.Data.Should().NotContainKey("redacted_output");
        suspendedTimeline.Data.Should().NotContainKey("input_mode");
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
    public void ApplyObservedPayloadToReport_ShouldPreserveSanitizedApprovalContent_AndHideSecureContent()
    {
        const string secret = "suspension-secret";
        var document = new WorkflowRunInsightReportDocument();

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            document,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "review",
                    SuspensionType = "human_approval",
                    Prompt = "Review the draft",
                    Content = $$"""{"draft":"ready","access_token":"{{secret}}"}""",
                    TimeoutSeconds = 3600,
                },
                1,
                "evt-review"),
            DateTimeOffset.UnixEpoch);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            document,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "secret",
                    SuspensionType = "secure_input",
                    Content = "must-not-materialize",
                    Secure = true,
                },
                2,
                "evt-secret"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var report = new WorkflowExecutionReadModelMapper().ToRunReport(document);
        var sanitized = WorkflowAuditReportSanitizer.Sanitize(report);

        var review = sanitized.Steps.Single(step => step.StepId == "review");
        review.SuspensionContent.Should().Contain("\"draft\":\"ready\"");
        review.SuspensionContent.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        review.SuspensionContent.Should().NotContain(secret);
        review.SuspensionTimeoutSeconds.Should().Be(3600);
        sanitized.Steps.Single(step => step.StepId == "secret")
            .SuspensionContent.Should().BeEmpty();
    }

    [Fact]
    public void ReportArtifact_ShouldOwnTimelineAndGraphMaterializationInputs()
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

        report.Timeline[0].Data["key"].Should().Be("value");
        report.Steps[0].RequestParameters["temperature"].Should().Be("0.2");
        report.Topology.Should().ContainSingle();

        var graph = new WorkflowRunInsightReportGraphMaterializer().Materialize(report);
        graph.Nodes.Should().Contain(x => x.NodeId == "root-actor");
        graph.Edges.Should().Contain(x => x.ToNodeId == "child-1");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldAggregateStepUsageAndClampNegativeMetrics()
    {
        var report = new WorkflowRunInsightReportDocument
        {
            Id = "root-actor",
            RootActorId = "root-actor",
            CommandId = "cmd-usage",
        };
        var timestamp = new DateTimeOffset(2026, 3, 18, 6, 15, 0, TimeSpan.Zero);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "step-negative",
                    Success = true,
                    Usage = new WorkflowUsageMetrics
                    {
                        PromptTokens = -10,
                        CompletionTokens = -20,
                        TotalTokens = -30,
                        Cost = -1,
                        LatencyMs = -100,
                    },
                },
                28,
                "evt-usage-negative"),
            timestamp);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "step-positive",
                    Success = true,
                    Usage = new WorkflowUsageMetrics
                    {
                        PromptTokens = 11,
                        CompletionTokens = 7,
                        TotalTokens = 18,
                        Model = "gpt-usage",
                        Cost = 0.42,
                        LatencyMs = 1234,
                    },
                },
                29,
                "evt-usage-positive"),
            timestamp.AddSeconds(1));

        report.Steps.Should().HaveCount(2);
        report.Steps[0].Usage.PromptTokens.Should().Be(0);
        report.Steps[0].Usage.CompletionTokens.Should().Be(0);
        report.Steps[0].Usage.TotalTokens.Should().Be(0);
        report.Steps[0].Usage.Model.Should().BeEmpty();
        report.Steps[0].Usage.Cost.Should().Be(0);
        report.Steps[0].Usage.LatencyMs.Should().Be(0);

        report.Usage.PromptTokens.Should().Be(11);
        report.Usage.CompletionTokens.Should().Be(7);
        report.Usage.TotalTokens.Should().Be(18);
        report.Usage.Model.Should().Be("gpt-usage");
        report.Usage.Cost.Should().Be(0.42);
        report.Usage.LatencyMs.Should().Be(1234);
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
                    ScopeId = "scope-current",
                    RunOrigin = "provisioned",
                    ScheduleId = "schedule-current",
                    Status = status,
                    Compiled = true,
                    CompilationError = "none",
                    Input = "hello",
                    FinalOutput = "done",
                    FinalError = "err",
                    SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                    DeadLetterFailedCompensationStepId = "refund_payment",
                    DeadLetterRemainingUncompensated = 2,
                    DeadLetterError = "refund failed",
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
                        AdmissionDigest = "admission-v3",
                    },
                    ExecutionStates =
                    {
                        ["workflow_execution_kernel"] = Any.Pack(new WorkflowExecutionKernelState
                        {
                            InputFileRefs = { BuildWorkflowFileRef("file-current") },
                        }),
                    },
                },
                includeEnvelopeTimestamp: false));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.RootActorId.Should().Be("root-actor");
        document.RunId.Should().Be("root-actor");
        document.CommandId.Should().Be("cmd-current");
        document.DefinitionActorId.Should().Be("definition-1");
        document.WorkflowName.Should().Be("wf-current");
        document.ScopeId.Should().Be("scope-current");
        document.RunOrigin.Should().Be("provisioned");
        document.ScheduleId.Should().Be("schedule-current");
        document.Status.Should().Be(status);
        document.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        document.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        document.DeadLetterRemainingUncompensated.Should().Be(2);
        document.DeadLetterError.Should().Be("refund failed");
        document.CapabilityAdmissionPlan.Should().NotBeNull();
        document.CapabilityAdmissionPlan.SchemaVersion.Should().Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        document.CapabilityAdmissionPlan.AdmissionDigest.Should().Be("admission-v3");
        document.StateVersion.Should().Be(1);
        document.Compiled.Should().BeTrue();
        document.ExecutionStateCount.Should().Be(1);
        document.Success.Should().Be(expectedSuccess);
        document.UpdatedAt.Should().Be(new DateTimeOffset(2026, 3, 18, 7, 0, 0, TimeSpan.Zero));
        var fileRef = document.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("file-current");
        fileRef.ArtifactId.Should().Be("workflow-file://file-current");
        fileRef.SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_1");
        fileRef.SourceResourceKey.Should().Be("resource-file-current");
        fileRef.FileName.Should().Be("file-current.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(1234);
        fileRef.Sha256.Should().Be("sha-file-current");
        fileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        fileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
        fileRef.OwnerRunId.Should().Be("run-owner");
        fileRef.OwnerScopeId.Should().Be("scope-owner");
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
    public async Task WorkflowExecutionCurrentStateProjector_WhenCommittedStateIsRelayedFromChild_ShouldSkipWrite()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 18, 7, 45, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    Success = true,
                    Output = "z\ny",
                },
                new WorkflowRunState
                {
                    WorkflowName = "child-level2",
                    Status = "completed",
                    FinalOutput = "z\ny",
                },
                publisherActorId: "child-run-actor"));

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldObserveCompensationMetricsFromCommittedFactsOnly()
    {
        using var metrics = new RecordingMeterListener();
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 18, 7, 50, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new CompensationRequestEvent
                {
                    RunId = "root-actor",
                    CompensationStepId = "refund_payment",
                },
                new WorkflowRunState { RunId = "root-actor", Status = "running" },
                version: 10,
                eventId: "evt-comp-request"));
        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new CompensationStepCompletedEvent
                {
                    RunId = "root-actor",
                    CompensationStepId = "refund_payment",
                    Success = true,
                },
                new WorkflowRunState { RunId = "root-actor", Status = "running" },
                version: 11,
                eventId: "evt-comp-success"));
        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompensationFailedEvent
                {
                    RunId = "root-actor",
                    FailedCompensationStepId = "refund_payment",
                    RemainingUncompensated = 2,
                    Error = "refund failed",
                },
                new WorkflowRunState
                {
                    RunId = "root-actor",
                    Status = "failed",
                    SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                },
                version: 12,
                eventId: "evt-comp-dead-letter"));
        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompensationFailedEvent
                {
                    RunId = "child-run",
                    FailedCompensationStepId = "child_refund",
                },
                new WorkflowRunState { RunId = "child-run", Status = "failed" },
                version: 13,
                eventId: "evt-child-comp-dead-letter",
                publisherActorId: "child-run"));

        dispatcher.Upserts.Should().HaveCount(3);
        metrics.Sum("aevatar.workflow.compensation.requested_total").Should().Be(1);
        metrics.Sum("aevatar.workflow.compensation.succeeded_total").Should().Be(1);
        metrics.Sum("aevatar.workflow.compensation.dead_lettered_total").Should().Be(1);
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldMapStartedAt_FromCommittedRunState()
    {
        // O2 (06-19-workflow-run-observatory): started_at is owned by the actor (WorkflowRunState.StartedAtUtc),
        // set once when the run starts. The projector maps it straight through — no prior-readmodel read.
        var startedAt = new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent { Success = true },
                new WorkflowRunState
                {
                    RunId = "root-actor",
                    Status = "completed",
                    StartedAtUtc = Timestamp.FromDateTimeOffset(startedAt),
                },
                includeEnvelopeTimestamp: false));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StartedAtUtcValue.Should().NotBeNull();
        document.StartedAtUtcValue.ToDateTimeOffset().Should().Be(startedAt);
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldLeaveStartedAtUnset_WhenStateHasNoStartFact()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new BindWorkflowRunDefinitionEvent { RunId = "root-actor" },
                new WorkflowRunState { RunId = "root-actor", Status = "bound" },
                includeEnvelopeTimestamp: false));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StartedAtUtcValue.Should().BeNull();
    }

    [Fact]
    public void WorkflowRunGraphArtifactMaterializer_ShouldDeriveFromReportAndDeduplicateNodesAndEdges()
    {
        var readModel = new WorkflowRunInsightReportDocument
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

    // 06-21: the graph carries the real run order — a step's NextStepId becomes a NEXT edge to that step
    // (with the branch taken as branchKey), but only when the next step is a known step node.
    [Fact]
    public void WorkflowRunGraphArtifactMaterializer_ShouldEmitNextEdgesFromStepFlow()
    {
        var readModel = new WorkflowRunInsightReportDocument
        {
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            WorkflowName = "wf-flow",
            Steps =
            [
                new WorkflowExecutionStepTrace { StepId = "a", StepType = "tool_call", Success = true, NextStepId = "b", BranchKey = "success" },
                new WorkflowExecutionStepTrace { StepId = "b", StepType = "llm_call", Success = true, NextStepId = "missing" },
            ],
        };

        var materialization = new WorkflowRunGraphArtifactMaterializer().Materialize(readModel);

        materialization.Edges.Should().Contain(x =>
            x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeNext &&
            x.FromNodeId == "step:actor-1:cmd-1:a" &&
            x.ToNodeId == "step:actor-1:cmd-1:b" &&
            x.Properties.ContainsKey("branchKey") &&
            x.Properties["branchKey"] == "success");
        // b -> "missing" is dropped because the target is not a known step node
        materialization.Edges.Should().NotContain(x =>
            x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeNext &&
            x.FromNodeId == "step:actor-1:cmd-1:b");
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
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2,
            DeadLetterError = "refund failed",
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
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            TopologySource = (WorkflowExecutionTopologySource)999,
            Success = true,
            FinalOutput = "done",
            FinalError = "ignored",
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    RequestedAt = new DateTimeOffset(2026, 3, 18, 7, 59, 0, TimeSpan.Zero),
                    CompletedAt = new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
                    SuspensionTimeoutSeconds = 30,
                },
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-2",
                },
            ],
            RoleReplies =
            [
                new WorkflowExecutionRoleReply
                {
                    Timestamp = new DateTimeOffset(2026, 3, 18, 8, 1, 0, TimeSpan.Zero),
                    RoleId = "assistant",
                },
                new WorkflowExecutionRoleReply
                {
                    RoleId = "system",
                },
            ],
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 3, 18, 8, 2, 0, TimeSpan.Zero),
                    Stage = "started",
                },
                new WorkflowExecutionTimelineEvent
                {
                    Stage = "finished",
                },
            ],
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 3,
                RequestedSteps = 2,
                CompletedSteps = 1,
                RoleReplyCount = 4,
            },
            Usage = new WorkflowUsageMetricsReadModel
            {
                PromptTokens = 25,
                CompletionTokens = 30,
                TotalTokens = 55,
                Model = "gpt-5.4",
                Cost = 0.66,
                LatencyMs = 432,
            },
        };

        var snapshot = mapper.ToActorSnapshot(currentState);
        var unknownSnapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-2",
            Status = "mystery",
        });
        var timelineItem = mapper.ToWorkflowRunTimelineExportItem(new WorkflowExecutionTimelineEvent
        {
            Timestamp = new DateTimeOffset(2026, 3, 18, 8, 1, 0, TimeSpan.Zero),
            Stage = "signal.waiting",
            Data = { ["signal_name"] = "continue" },
        });
        var node = mapper.ToWorkflowRunGraphExportNode(new ProjectionGraphNode
        {
            NodeId = "node-1",
            NodeType = "Actor",
            Properties = { ["key"] = "value" },
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 8, 2, 0, TimeSpan.Zero),
        });
        var edge = mapper.ToWorkflowRunGraphExportEdge(new ProjectionGraphEdge
        {
            EdgeId = "edge-1",
            FromNodeId = "node-1",
            ToNodeId = "node-2",
            EdgeType = "CHILD_OF",
            Properties = { ["kind"] = "runtime" },
            UpdatedAt = new DateTimeOffset(2026, 3, 18, 8, 3, 0, TimeSpan.Zero),
        });
        var subgraph = mapper.ToWorkflowRunGraphExportSubgraph(
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
        snapshot.WorkflowName.Should().BeEmpty();
        snapshot.CompletionStatus.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunCompletionStatus.Running);
        snapshot.LastSuccess.Should().BeNull();
        snapshot.LastOutput.Should().BeEmpty();
        snapshot.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        snapshot.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        snapshot.DeadLetterRemainingUncompensated.Should().Be(2);
        snapshot.DeadLetterError.Should().Be("refund failed");
        snapshot.TotalSteps.Should().Be(0);
        snapshot.RoleReplyCount.Should().Be(0);

        unknownSnapshot.CompletionStatus.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunCompletionStatus.Unknown);
        timelineItem.Data.Should().Contain(new KeyValuePair<string, string>("signal_name", "continue"));
        node.Properties.Should().Contain(new KeyValuePair<string, string>("key", "value"));
        edge.Properties.Should().Contain(new KeyValuePair<string, string>("kind", "runtime"));
        subgraph.RootNodeId.Should().Be("node-1");
        subgraph.Nodes.Should().ContainSingle();
        subgraph.Edges.Should().ContainSingle();

        var mappedReport = mapper.ToRunReport(report);
        mappedReport.ProjectionScope.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunProjectionScope.RunIsolated);
        mappedReport.TopologySource.Should().Be(Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunTopologySource.Unknown);
        mappedReport.Steps.Should().HaveCount(2);
        mappedReport.Steps[0].RequestedAt.Should().NotBeNull();
        mappedReport.Steps[0].CompletedAt.Should().NotBeNull();
        mappedReport.Steps[0].SuspensionTimeoutSeconds.Should().Be(30);
        mappedReport.Steps[1].RequestedAt.Should().BeNull();
        mappedReport.Steps[1].CompletedAt.Should().BeNull();
        mappedReport.Steps[1].SuspensionTimeoutSeconds.Should().BeNull();
        mappedReport.Usage.TotalTokens.Should().Be(55);
        mappedReport.Usage.Model.Should().Be("gpt-5.4");
        mappedReport.RoleReplies[0].Timestamp.Should().Be(new DateTimeOffset(2026, 3, 18, 8, 1, 0, TimeSpan.Zero));
        mappedReport.RoleReplies[1].Timestamp.Should().Be(default);
        mappedReport.Timeline[0].Timestamp.Should().Be(new DateTimeOffset(2026, 3, 18, 8, 2, 0, TimeSpan.Zero));
        mappedReport.Timeline[1].Timestamp.Should().Be(default);
        mapper.ToRunReport(new WorkflowRunInsightReportDocument
        {
            ProjectionScope = (WorkflowExecutionProjectionScope)999,
            TopologySource = WorkflowExecutionTopologySource.CommittedProjection,
            SummaryValue = null,
        }).Should().Match<Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunReport>(mapped =>
            mapped.ProjectionScope == Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunProjectionScope.Unknown &&
            mapped.TopologySource == Aevatar.Workflow.Application.Abstractions.Queries.WorkflowRunTopologySource.CommittedProjection &&
            mapped.Summary.TotalSteps == 0);
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
        string publisherActorId = "root-actor",
        bool includeEnvelopeTimestamp = true)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = includeEnvelopeTimestamp
                ? Timestamp.FromDateTime(DateTime.UtcNow)
                : null,
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
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = $"resource-{fileId}",
            FileName = $"{fileId}.pdf",
            MediaType = "application/pdf",
            SizeBytes = 1234,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
            OwnerRunId = "run-owner",
            OwnerScopeId = "scope-owner",
        };

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

    private sealed class RecordingMeterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string InstrumentName, long Measurement)> _measurements = [];

        public RecordingMeterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Aevatar.Workflow" &&
                    instrument.Name.StartsWith("aevatar.workflow.compensation.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, state) =>
                {
                    _ = tags;
                    _ = state;
                    _measurements.Add((instrument.Name, measurement));
                });
            _listener.Start();
        }

        public long Sum(string instrumentName)
        {
            _listener.RecordObservableInstruments();
            return _measurements
                .Where(x => string.Equals(x.InstrumentName, instrumentName, StringComparison.Ordinal))
                .Sum(x => x.Measurement);
        }

        public void Dispose() => _listener.Dispose();
    }
}
