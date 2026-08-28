using System.Diagnostics.Metrics;
using System.Text;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Queries;
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
        var opaqueCredential = new string('B', 48);
        var observedAt = new DateTimeOffset(2026, 3, 18, 3, 0, 0, TimeSpan.Zero);
        var context = CreateContext();

        var runningReport = new WorkflowRunInsightReportDocument
        {
            WorkflowName = "existing-name",
            CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal,
            ReportVersion = "3.0",
        };
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            runningReport,
            context,
            new WorkflowRunState
            {
                LastCommandId = "cmd-running",
                Status = "running",
                FinalError = $"provider failed opaque={opaqueCredential}",
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
        runningReport.ReportVersion.Should().Be("3.0");
        runningReport.FinalError.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        runningReport.FinalError.Should().NotContain(opaqueCredential);
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

        report.ReportVersion.Should().Be("3.1");
        report.ProjectionScope.Should().Be(WorkflowExecutionProjectionScope.RunIsolated);
        report.TopologySource.Should().Be(WorkflowExecutionTopologySource.CommittedProjection);
        report.WorkflowName.Should().BeEmpty();
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Unknown);
        report.Success.Should().BeNull();
        report.CreatedAt.Should().Be(observedAt);
        report.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public void ApplyReportBase_ShouldResolveFailedStoppedAndTimedOutStatuses()
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

        var timedOutReport = new WorkflowRunInsightReportDocument();
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            timedOutReport,
            context,
            new WorkflowRunState
            {
                WorkflowName = "wf-timed-out",
                Status = "timed_out",
                FinalError = "deadline exceeded",
            },
            new StateEvent
            {
                Version = 5,
                EventId = "evt-timed-out",
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

        timedOutReport.WorkflowName.Should().Be("wf-timed-out");
        timedOutReport.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.TimedOut);
        timedOutReport.Success.Should().BeFalse();
        timedOutReport.EndedAt.Should().Be(observedAt);
        var mappedTimedOutReport = new WorkflowExecutionReadModelMapper().ToRunReport(timedOutReport);
        mappedTimedOutReport.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.TimedOut);
        mappedTimedOutReport.Success.Should().BeFalse();
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldTrackObservedWorkflowArtifactsAcrossBranches()
    {
        var parameterCredential = new string('P', 48);
        var assignedCredential = new string('Q', 48);
        var annotationCredential = new string('R', 48);
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
                        ["opaque"] = $"value={parameterCredential}",
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
                    Output = string.Join(' ', Enumerable.Repeat("step-output", 26)),
                    Error = "tool failed",
                    WorkerId = "worker-1",
                    NextStepId = "step-2",
                    BranchKey = "fallback",
                    AssignedVariable = "answer",
                    AssignedValue = $"value={assignedCredential}",
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.AuthorizationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results =
                        {
                            new WorkflowFileItemResult
                            {
                                Index = 0,
                                Success = false,
                                Output = "{\"fooSecret\":\"must-not-persist\",\"detail\":\"partial\"}",
                                Error = "file failed",
                                FileRef = new WorkflowFileRef
                                {
                                    FileId = "file-alpha",
                                    FileName = "synthetic.contact@example.test",
                                },
                            },
                        },
                    },
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Kind = AgreementDecisionKind.Inconclusive,
                        BranchKey = "manual-review",
                        WinnerCandidateId = "candidate-alpha",
                        Output = "{\"service_password\":\"must-not-persist\",\"detail\":\"vote\"}",
                        Reason = "agreement failed",
                        LabelCounts =
                        {
                            ["approve"] = 1,
                            ["reject"] = 1,
                        },
                    },
                    Annotations =
                    {
                        ["reason"] = "timeout",
                        ["trace"] = $"value={annotationCredential}",
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
        step.FailureOutput.Should().Be(string.Join(' ', Enumerable.Repeat("step-output", 26)));
        step.FailureOutputTruncated.Should().BeFalse();
        step.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        step.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.AuthorizationFailure);
        step.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        step.FileItemResults.Should().NotBeNull();
        step.FileItemResults.Results.Should().ContainSingle();
        step.FileItemResults.Results[0].Output.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.FileItemResults.Results[0].Output.Should().NotContain("must-not-persist");
        step.FileItemResults.Results[0].FileRef.FileName.Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        step.VoteAgreementDecision.Should().NotBeNull();
        step.VoteAgreementDecision.Output.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.VoteAgreementDecision.Output.Should().NotContain("must-not-persist");
        step.VoteAgreementDecision.LabelCounts.Should().Contain(new KeyValuePair<string, int>("approve", 1));
        step.NextStepId.Should().Be("step-2");
        step.BranchKey.Should().Be("fallback");
        step.AssignedVariable.Should().Be("answer");
        step.AssignedValue.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.AssignedValue.Should().NotContain(assignedCredential);
        step.SuspensionType.Should().Be("human_input");
        step.SuspensionPrompt.Should().Be("Need approval");
        step.SuspensionTimeoutSeconds.Should().BeNull();
        step.RequestedVariableName.Should().Be("approval");
        step.RequestParameters.Should().Contain(new KeyValuePair<string, string>("temperature", "0.2"));
        step.RequestParameters["opaque"].Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.RequestParameters["opaque"].Should().NotContain(parameterCredential);
        step.CompletionAnnotations.Should().Contain(new KeyValuePair<string, string>("reason", "timeout"));
        step.CompletionAnnotations["trace"].Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.CompletionAnnotations["trace"].Should().NotContain(annotationCredential);

        report.Topology.Should().HaveCount(2);
        report.Topology.Should().Contain(x => x.Parent == "root-actor" && x.Child == "role-actor-1");
        report.Topology.Should().Contain(x => x.Parent == "root-actor" && x.Child == "child-run-1");

        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("role-actor-1");
        report.RoleReplies[0].SessionId.Should().Be("session-1");
        report.RoleReplies[0].ContentLength.Should().Be(8);

        report.Timeline.Should().Contain(x => x.Stage == "workflow.start" && x.Message == "command=cmd-1");
        report.Timeline.Should().Contain(x => x.Stage == "step.request" && x.StepId == "step-1");
        report.Timeline.Should().Contain(x =>
            x.Stage == "step.failed" &&
            x.StepId == "step-1" &&
            x.Message == "tool failed" &&
            x.Data["error"] == "tool failed");
        var suspendedTimeline = report.Timeline.Single(x => x.Stage == "workflow.suspended" && x.StepId == "step-1");
        suspendedTimeline.Data.Should().ContainKey("channel").WhoseValue.Should().Be("ui");
        suspendedTimeline.Data.Should().ContainKey("variable").WhoseValue.Should().Be("approval");
        suspendedTimeline.Data.Should().ContainKey("secure").WhoseValue.Should().Be("true");
        suspendedTimeline.Data.Should().ContainKey("redacted_output").WhoseValue.Should().Be("[captured]");
        suspendedTimeline.Data.Should().NotContainKey("input_mode");
        report.Timeline.Should().Contain(x => x.Stage == "signal.waiting" && x.Data["timeout_ms"] == "900");
        report.Timeline.Should().Contain(x => x.Stage == "signal.buffered");
        report.Timeline.Count(x => x.Stage == "tool.call").Should().Be(2);
        report.Timeline.Should().Contain(x =>
            x.Stage == "workflow.failed" &&
            x.Message == "failed-hard" &&
            x.Data["error"] == "failed-hard");
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
    public void ApplyObservedPayloadToReport_ShouldKeepRetryAttemptsSeparateAndClearSnapshotAfterSuccess()
    {
        const int maxFailureOutputUtf8Bytes = 64 * 1024;
        const string secret = "short-secret-value";
        var opaqueCredential = new string('A', 48);
        var largeOutput = $"BEGIN token={secret} opaque={opaqueCredential} " +
                          string.Concat(Enumerable.Repeat("payload-block ", 7000)) +
                          "END-🙂";
        var report = new WorkflowRunInsightReportDocument();

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "retryable-step",
                    ExecutionId = "execution-first",
                    DisplayName = "Normalize person",
                    StepType = "code_execute",
                    TargetRole = "normalizer",
                    Parameters =
                    {
                        ["language"] = "python",
                        ["attempt_identity"] = "first",
                    },
                },
                1,
                "evt-first-requested"),
            DateTimeOffset.UnixEpoch);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    StepId = "retryable-step",
                    SuspensionType = "tool_approval",
                    Prompt = "Approve first attempt",
                    Content = "first-attempt-content",
                    TimeoutSeconds = 30,
                    VariableName = "approval",
                    ToolApproval = new WorkflowToolApprovalSuspension
                    {
                        ExecutionId = "execution-first",
                        ToolName = "code_execute",
                        ToolCallId = "call-first",
                        ApprovalRequestId = "approval-first",
                    },
                },
                2,
                "evt-first-suspended"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "retryable-step",
                    ExecutionId = "execution-first",
                    Success = false,
                    Output = largeOutput,
                    Error = $"provider failed token={secret}",
                    WorkerId = "worker-first",
                    NextStepId = "fallback",
                    BranchKey = "failure",
                    AssignedVariable = "normalized",
                    AssignedValue = "unavailable",
                    Annotations = { ["attempt"] = "first" },
                    Usage = new WorkflowUsageMetrics { PromptTokens = 3, TotalTokens = 3 },
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results = { new WorkflowFileItemResult { Index = 0, Error = "file failed" } },
                    },
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Kind = AgreementDecisionKind.Inconclusive,
                        Reason = "no valid candidate",
                    },
                },
                3,
                "evt-failed"),
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        var failed = report.Steps.Should().ContainSingle().Subject;
        failed.FailureOutputTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(failed.FailureOutput).Should().BeLessThanOrEqualTo(maxFailureOutputUtf8Bytes);
        failed.FailureOutput.Should().StartWith($"BEGIN token={WorkflowAuditTextSanitizer.RedactedValue}");
        failed.FailureOutput.Should().Contain(WorkflowAuditTextSanitizer.HeadTailTruncationMarker);
        failed.FailureOutput.Should().EndWith("END-🙂");
        failed.FailureOutput.Should().NotContain(secret);
        failed.FailureOutput.Should().NotContain(opaqueCredential);
        failed.OutputPreview.Should().NotContain(opaqueCredential);
        failed.OutputPreview.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        failed.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        failed.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.ConfigurationFailure);
        failed.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        report.Timeline.Should().ContainSingle(item =>
            item.Stage == "step.failed" &&
            item.Message == $"provider failed token={WorkflowAuditTextSanitizer.RedactedValue}" &&
            item.Data["error"] == item.Message);

        var retainedFailureOutput = failed.FailureOutput;
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "retryable-step",
                    ExecutionId = "execution-second",
                    StepType = "connector_call",
                    DisplayName = "Send normalized result",
                    TargetRole = "mailer",
                    Parameters =
                    {
                        ["connector"] = "smtp",
                        ["attempt_identity"] = "second",
                    },
                },
                4,
                "evt-retry-requested"),
            DateTimeOffset.UnixEpoch.AddSeconds(5));

        var waiting = report.Steps.Should().ContainSingle().Subject;
        waiting.Outcome.Should().Be(WorkflowExecutionStepOutcomeReadModel.Waiting);
        waiting.StepType.Should().Be("connector_call");
        waiting.TargetRole.Should().Be("mailer");
        waiting.RequestParameters.Should().BeEmpty();
        waiting.RequestEvidenceReference.Should().NotBeNull();
        waiting.RequestEvidenceReference.ExecutionId.Should().Be("execution-second");
        waiting.RequestedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(5));
        waiting.CompletedAt.Should().BeNull();
        waiting.Success.Should().BeNull();
        waiting.WorkerId.Should().BeEmpty();
        waiting.OutputPreview.Should().BeEmpty();
        waiting.Error.Should().BeEmpty();
        waiting.FailureOutput.Should().BeEmpty();
        waiting.FailureOutputTruncated.Should().BeFalse();
        waiting.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.Unspecified);
        waiting.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.Unspecified);
        waiting.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Unspecified);
        waiting.FileItemResults.Should().BeNull();
        waiting.VoteAgreementDecision.Should().BeNull();
        waiting.CompletionAnnotations.Should().BeEmpty();
        waiting.NextStepId.Should().BeEmpty();
        waiting.BranchKey.Should().BeEmpty();
        waiting.AssignedVariable.Should().BeEmpty();
        waiting.AssignedValue.Should().BeEmpty();
        waiting.Usage.TotalTokens.Should().Be(0);
        waiting.SuspensionType.Should().BeEmpty();
        waiting.SuspensionPrompt.Should().BeEmpty();
        waiting.SuspensionContent.Should().BeEmpty();
        waiting.SuspensionTimeoutSeconds.Should().BeNull();
        waiting.RequestedVariableName.Should().BeEmpty();
        waiting.ToolApprovalValue.Should().BeNull();

        waiting.LatestFailedAttempt.Should().NotBeNull();
        var failedAttempt = waiting.LatestFailedAttempt!;
        failedAttempt.DisplayName.Should().Be("Normalize person");
        failedAttempt.StepType.Should().Be("code_execute");
        failedAttempt.TargetRole.Should().Be("normalizer");
        failedAttempt.RequestParameters.Should().BeEmpty();
        failedAttempt.RequestEvidenceReference.Should().NotBeNull();
        failedAttempt.RequestEvidenceReference.ExecutionId.Should().Be("execution-first");
        failedAttempt.RequestedAt.Should().Be(DateTimeOffset.UnixEpoch);
        failedAttempt.CompletedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(2));
        failedAttempt.DurationMs.Should().Be(2000);
        failedAttempt.WorkerId.Should().Be("worker-first");
        failedAttempt.Error.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        failedAttempt.FailureOutput.Should().Be(retainedFailureOutput);
        failedAttempt.FailureOutputTruncated.Should().BeTrue();
        failedAttempt.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        failedAttempt.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.ConfigurationFailure);
        failedAttempt.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        failedAttempt.CompletionAnnotations.Should().Contain("attempt", "first");
        failedAttempt.NextStepId.Should().Be("fallback");
        failedAttempt.BranchKey.Should().Be("failure");
        failedAttempt.AssignedVariable.Should().Be("normalized");
        failedAttempt.Usage.TotalTokens.Should().Be(3);
        failedAttempt.FileItemResults.Results.Should().ContainSingle().Which.Error.Should().Be("file failed");
        failedAttempt.VoteAgreementDecision.Reason.Should().Be("no valid candidate");
        failedAttempt.SuspensionType.Should().Be("tool_approval");
        failedAttempt.SuspensionPrompt.Should().Be("Approve first attempt");
        failedAttempt.SuspensionContent.Should().Be("first-attempt-content");
        failedAttempt.SuspensionTimeoutSeconds.Should().Be(30);
        failedAttempt.RequestedVariableName.Should().Be("approval");
        failedAttempt.ToolApprovalValue.ExecutionId.Should().Be("execution-first");

        report.RequestEvidenceById.Should().HaveCount(2);
        var firstEvidence = report.RequestEvidenceById[failedAttempt.RequestEvidenceReference.EvidenceId];
        var secondEvidence = report.RequestEvidenceById[waiting.RequestEvidenceReference.EvidenceId];
        firstEvidence.ParametersMap.Should().Contain("attempt_identity", "first");
        secondEvidence.ParametersMap.Should().Contain("attempt_identity", "second");
        firstEvidence.SourceEventId.Should().Be("evt-first-requested");
        secondEvidence.SourceEventId.Should().Be("evt-retry-requested");

        var requestTimeline = report.Timeline.Where(item => item.Stage == "step.request").ToArray();
        requestTimeline.Should().HaveCount(2);
        requestTimeline[0].Data.Should().BeEmpty();
        requestTimeline[1].Data.Should().BeEmpty();
        requestTimeline[0].RequestEvidenceReference.Should().Be(failedAttempt.RequestEvidenceReference);
        requestTimeline[1].RequestEvidenceReference.Should().Be(waiting.RequestEvidenceReference);

        var mapped = new WorkflowExecutionReadModelMapper().ToRunReport(report);
        mapped.Steps.Single().RequestParameters.Should().Contain("attempt_identity", "second");
        mapped.Steps.Single().LatestFailedAttempt!.RequestParameters.Should().Contain("attempt_identity", "first");
        mapped.Timeline.Where(item => item.Stage == "step.request")
            .Select(item => item.Data["attempt_identity"])
            .Should().Equal("first", "second");

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "retryable-step",
                    ExecutionId = "execution-second",
                    Success = true,
                    Output = "recovered",
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.AuthorizationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Kind = AgreementDecisionKind.Agreed,
                        Output = "accepted",
                    },
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results =
                        {
                            new WorkflowFileItemResult { Index = 0, Success = true, Output = "ready" },
                        },
                    },
                },
                5,
                "evt-recovered"),
            DateTimeOffset.UnixEpoch.AddSeconds(8));

        var recovered = report.Steps.Should().ContainSingle().Subject;
        recovered.FailureOutput.Should().BeEmpty();
        recovered.FailureOutputTruncated.Should().BeFalse();
        recovered.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.Unspecified);
        recovered.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.Unspecified);
        recovered.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Unspecified);
        recovered.LatestFailedAttempt.Should().BeNull();
        recovered.VoteAgreementDecision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        recovered.FileItemResults.Results.Should().ContainSingle().Which.Output.Should().Be("ready");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldReuseRequestEvidenceWhenPendingDispatchIsRecommitted()
    {
        var report = new WorkflowRunInsightReportDocument();
        var request = new StepRequestEvent
        {
            StepId = "pending-step",
            ExecutionId = "execution-1",
            StepType = "assign",
            Parameters = { ["value"] = "stable" },
        };
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(request, 1, "evt-first"),
            DateTimeOffset.UnixEpoch);

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(request.Clone(), 2, "evt-recommitted"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var evidence = report.RequestEvidenceById.Values.Should().ContainSingle().Subject;
        evidence.SourceEventId.Should().Be("evt-first");
        evidence.ParametersMap.Should().Contain("value", "stable");
        report.Steps.Should().ContainSingle()
            .Which.RequestEvidenceReference.Should().BeEquivalentTo(
                new WorkflowStepRequestEvidenceReference
                {
                    EvidenceId = evidence.EvidenceId,
                    ExecutionId = "execution-1",
                    SourceEventId = "evt-first",
                });
        report.Timeline.Where(item => item.Stage == "step.request")
            .Should().HaveCount(2)
            .And.OnlyContain(item => item.RequestEvidenceReference!.EvidenceId == evidence.EvidenceId);
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldRejectDifferentParametersForTheSameExecutionEvidence()
    {
        var report = new WorkflowRunInsightReportDocument();
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "immutable-step",
                    ExecutionId = "execution-1",
                    StepType = "assign",
                    Parameters = { ["value"] = "first" },
                },
                1,
                "evt-first"),
            DateTimeOffset.UnixEpoch);
        var beforeConflict = report.Clone();

        var act = () => WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepRequestEvent
                {
                    StepId = "immutable-step",
                    ExecutionId = "execution-1",
                    StepType = "assign",
                    Parameters = { ["value"] = "different" },
                },
                2,
                "evt-conflict"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already bound to different immutable content*");
        report.Should().Be(beforeConflict, "an immutable-evidence conflict must not partially mutate the report");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldBoundNestedEvidenceAndPreserveSourceTruncationFlags()
    {
        var largeEvidence = "BEGIN-" +
                            new string('界', WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes) +
                            "-END-🙂";
        var report = new WorkflowRunInsightReportDocument();

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "locally-bounded",
                    Success = false,
                    Output = "failed",
                    Error = "nested evidence exceeded its bound",
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results =
                        {
                            new WorkflowFileItemResult
                            {
                                Index = 0,
                                Success = false,
                                Output = largeEvidence,
                                Error = largeEvidence,
                            },
                        },
                    },
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Kind = AgreementDecisionKind.Inconclusive,
                        Output = largeEvidence,
                        Reason = largeEvidence,
                    },
                },
                1,
                "evt-locally-bounded"),
            DateTimeOffset.UnixEpoch);

        var bounded = report.Steps.Single(step => step.StepId == "locally-bounded");
        var fileResult = bounded.FileItemResults.Results.Should().ContainSingle().Subject;
        fileResult.OutputTruncated.Should().BeTrue();
        fileResult.ErrorTruncated.Should().BeTrue();
        bounded.VoteAgreementDecision.OutputTruncated.Should().BeTrue();
        bounded.VoteAgreementDecision.ReasonTruncated.Should().BeTrue();
        foreach (var text in new[] { fileResult.Output, fileResult.Error })
        {
            Encoding.UTF8.GetByteCount(text)
                .Should().BeLessThanOrEqualTo(WorkflowFileItemResultProjectionContract.MaxEvidenceUtf8Bytes);
            text.Should().StartWith("BEGIN-");
            text.Should().Contain(WorkflowAuditTextSanitizer.HeadTailTruncationMarker);
            text.Should().EndWith("-END-🙂");
        }
        foreach (var text in new[]
                 {
                     bounded.VoteAgreementDecision.Output,
                     bounded.VoteAgreementDecision.Reason,
                 })
        {
            Encoding.UTF8.GetByteCount(text)
                .Should().BeLessThanOrEqualTo(WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes);
            text.Should().StartWith("BEGIN-");
            text.Should().Contain(WorkflowAuditTextSanitizer.HeadTailTruncationMarker);
            text.Should().EndWith("-END-🙂");
        }

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "source-bounded",
                    Success = false,
                    Output = "failed",
                    Error = "source already bounded the nested evidence",
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results =
                        {
                            new WorkflowFileItemResult
                            {
                                Index = 0,
                                Success = false,
                                Output = "short output",
                                Error = "short error",
                                OutputTruncated = true,
                                ErrorTruncated = true,
                            },
                        },
                    },
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Kind = AgreementDecisionKind.Inconclusive,
                        Output = "short output",
                        Reason = "short reason",
                        OutputTruncated = true,
                        ReasonTruncated = true,
                    },
                },
                2,
                "evt-source-bounded"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var sourceBounded = report.Steps.Single(step => step.StepId == "source-bounded");
        var sourceFileResult = sourceBounded.FileItemResults.Results.Should().ContainSingle().Subject;
        sourceFileResult.Output.Should().Be("short output");
        sourceFileResult.Error.Should().Be("short error");
        sourceFileResult.OutputTruncated.Should().BeTrue();
        sourceFileResult.ErrorTruncated.Should().BeTrue();
        sourceBounded.VoteAgreementDecision.Output.Should().Be("short output");
        sourceBounded.VoteAgreementDecision.Reason.Should().Be("short reason");
        sourceBounded.VoteAgreementDecision.OutputTruncated.Should().BeTrue();
        sourceBounded.VoteAgreementDecision.ReasonTruncated.Should().BeTrue();
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldBoundFileItemResultCountAndPreserveSourceCount()
    {
        var source = new WorkflowFileItemResultSet();
        source.Results.Add(Enumerable.Range(
                0,
                WorkflowFileItemResultProjectionContract.MaxRetainedResults + 5)
            .Select(index => new WorkflowFileItemResult
            {
                Index = index,
                Success = index % 2 == 0,
                Output = $"output-{index}",
            }));
        var report = new WorkflowRunInsightReportDocument();

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "locally-bounded-items",
                    Success = false,
                    Error = "some file items failed",
                    FileItemResults = source,
                },
                1,
                "evt-locally-bounded-items"),
            DateTimeOffset.UnixEpoch);

        var locallyBounded = report.Steps.Should().ContainSingle().Subject.FileItemResults;
        locallyBounded.SourceResultCount.Should().Be(source.Results.Count);
        locallyBounded.ResultsTruncated.Should().BeTrue();
        locallyBounded.Results.Should().HaveCount(WorkflowFileItemResultProjectionContract.MaxRetainedResults);
        locallyBounded.Results.Select(item => item.Index).Should().Equal(
            Enumerable.Range(0, WorkflowFileItemResultProjectionContract.MaxRetainedResults / 2)
                .Concat(Enumerable.Range(
                    source.Results.Count - WorkflowFileItemResultProjectionContract.MaxRetainedResults / 2,
                    WorkflowFileItemResultProjectionContract.MaxRetainedResults / 2)));

        var upstreamBounded = locallyBounded.Clone();
        upstreamBounded.SourceResultCount = 100;
        upstreamBounded.ResultsTruncated = true;
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "upstream-bounded-items",
                    Success = false,
                    Error = "upstream bounded file items",
                    FileItemResults = upstreamBounded,
                },
                2,
                "evt-upstream-bounded-items"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var reSanitized = report.Steps.Single(step => step.StepId == "upstream-bounded-items").FileItemResults;
        reSanitized.SourceResultCount.Should().Be(100);
        reSanitized.ResultsTruncated.Should().BeTrue();
        reSanitized.Results.Select(item => item.Index)
            .Should().Equal(locallyBounded.Results.Select(item => item.Index));

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            report,
            PackStateEvent(
                new StepCompletedEvent
                {
                    StepId = "upstream-bounded-unknown-count",
                    Success = false,
                    Error = "upstream source count is unknown",
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        ResultsTruncated = true,
                        Results =
                        {
                            new WorkflowFileItemResult { Index = 42, Output = "retained" },
                        },
                    },
                },
                3,
                "evt-upstream-bounded-unknown-count"),
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        var unknownCount = report.Steps
            .Single(step => step.StepId == "upstream-bounded-unknown-count")
            .FileItemResults;
        unknownCount.SourceResultCount.Should().Be(0);
        unknownCount.ResultsTruncated.Should().BeTrue();
        unknownCount.Results.Should().ContainSingle().Which.Index.Should().Be(42);
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
    public void ApplyObservedPayloadToReport_ShouldPreserveTypedToolApprovalIdentity()
    {
        var document = new WorkflowRunInsightReportDocument();

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            document,
            PackStateEvent(
                new WorkflowSuspendedEvent
                {
                    RunId = "run-tool",
                    StepId = "create_approval",
                    SuspensionType = "tool_approval",
                    Prompt = "Approve tool execution?",
                    ToolApproval = new WorkflowToolApprovalSuspension
                    {
                        ExecutionId = "exec-alpha",
                        ToolName = "nyxid_proxy",
                        ToolCallId = "call-alpha",
                        ApprovalRequestId = "approval-alpha",
                    },
                },
                1,
                "evt-tool-approval"),
            DateTimeOffset.UnixEpoch);

        var approval = new WorkflowExecutionReadModelMapper()
            .ToRunReport(document)
            .Steps.Should().ContainSingle().Subject;
        approval.SuspensionType.Should().Be("tool_approval");
        approval.ToolApproval.Should().NotBeNull();
        approval.ToolApproval!.ExecutionId.Should().Be("exec-alpha");
        approval.ToolApproval.ToolName.Should().Be("nyxid_proxy");
        approval.ToolApproval.ToolCallId.Should().Be("call-alpha");
        approval.ToolApproval.ApprovalRequestId.Should().Be("approval-alpha");
    }

    [Fact]
    public void ApplyObservedPayloadToReport_ShouldMaterializeToolApprovalResumeRejection()
    {
        var document = new WorkflowRunInsightReportDocument
        {
            RootActorId = "run-tool",
        };

        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            document,
            PackStateEvent(
                new WorkflowToolApprovalResumeRejectedEvent
                {
                    RunId = "run-tool",
                    StepId = "create_approval",
                    SubmittedApproval = new WorkflowToolApprovalResume
                    {
                        ExecutionId = "exec-alpha",
                        ToolCallId = "call-alpha",
                        ApprovalRequestId = "approval-stale",
                    },
                    Reason = WorkflowToolApprovalResumeRejectionReason.IdentityMismatch,
                },
                2,
                "evt-tool-approval-resume-rejected"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var timeline = new WorkflowExecutionReadModelMapper()
            .ToRunReport(document)
            .Timeline.Should().ContainSingle().Subject;
        timeline.Stage.Should().Be("tool_approval.resume_rejected");
        timeline.StepId.Should().Be("create_approval");
        timeline.StepType.Should().Be("tool_call");
        timeline.Data.Should().ContainKey("reason").WhoseValue.Should().Be("IdentityMismatch");
        timeline.Data.Should().ContainKey("execution_id").WhoseValue.Should().Be("exec-alpha");
        timeline.Data.Should().ContainKey("tool_call_id").WhoseValue.Should().Be("call-alpha");
        timeline.Data.Should().ContainKey("approval_request_id").WhoseValue.Should().Be("approval-stale");
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
        graph.Nodes.Should().Contain(x =>
            x.NodeId == "actor:root-actor:cmd-3:child-1" &&
            x.Properties["actorId"] == "child-1");
        graph.Edges.Should().Contain(x => x.ToNodeId == "actor:root-actor:cmd-3:child-1");
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
        var finalErrorCredential = new string('C', 48);
        var deadLetterCredential = new string('D', 48);
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
                    CompilationError = "{\"lark_app_token\":\"compile-secret\",\"message\":\"compile failed\"}",
                    Input = "hello",
                    FinalOutput = "done",
                    FinalError = $"err opaque={finalErrorCredential}",
                    TerminalValueLifecycleFailureKind =
                        WorkflowValueLifecycleFailureKind.ReleasedValueAccessed,
                    SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                    DeadLetterFailedCompensationStepId = "refund_payment",
                    DeadLetterRemainingUncompensated = 2,
                    DeadLetterError = $"refund failed opaque={deadLetterCredential}",
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
        document.FinalError.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        document.FinalError.Should().NotContain(finalErrorCredential);
        document.TerminalValueLifecycleFailureKind.Should().Be(
            WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
        document.DeadLetterError.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        document.DeadLetterError.Should().NotContain(deadLetterCredential);
        document.CapabilityAdmissionPlan.Should().NotBeNull();
        document.CapabilityAdmissionPlan.SchemaVersion.Should().Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        document.CapabilityAdmissionPlan.AdmissionDigest.Should().Be("admission-v3");
        document.StateVersion.Should().Be(1);
        document.Compiled.Should().BeTrue();
        document.CompilationError.Should().Contain("compile failed");
        document.CompilationError.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        document.CompilationError.Should().NotContain("compile-secret");
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

    [Theory]
    [InlineData("running", "awaiting_tool_approval", WorkflowRunCompletionStatus.AwaitingToolApproval, null)]
    [InlineData("timed_out", "timed_out", WorkflowRunCompletionStatus.TimedOut, false)]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldPreserveTerminalStatusOverPendingToolApproval(
        string stateStatus,
        string expectedStatus,
        WorkflowRunCompletionStatus expectedCompletionStatus,
        bool? expectedSuccess)
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-04T09:00:00+00:00")));
        var state = new WorkflowRunState
        {
            RunId = "run-approval",
            Status = stateStatus,
        };
        state.ExecutionStates["tool_call"] = Any.Pack(new ToolCallModuleState
        {
            PendingApprovals =
            {
                ["approval-key"] = new PendingToolCallApprovalState
                {
                    RunId = "run-approval",
                    StepId = "write_record",
                    ExecutionId = "exec-alpha",
                    ToolName = "nyxid_proxy",
                    ToolCallId = "call-alpha",
                    ApprovalRequestId = "approval-alpha",
                },
            },
        });

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowSuspendedEvent
                {
                    RunId = "run-approval",
                    StepId = "write_record",
                    SuspensionType = "tool_approval",
                },
                state));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Status.Should().Be(expectedStatus);
        document.Success.Should().Be(expectedSuccess);
        new WorkflowExecutionReadModelMapper()
            .ToActorSnapshot(document)
            .CompletionStatus.Should().Be(expectedCompletionStatus);
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldExposeActivityFailureAndDelayStepIds()
    {
        var opaqueCredential = new string('E', 48);
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-10T09:00:00+00:00")));
        var state = new WorkflowRunState
        {
            RunId = "run-activity",
            ScopeId = "scope-activity",
            Status = "failed",
            FinalError = $"step failed opaque={opaqueCredential}",
        };
        state.ExecutionStates["workflow_execution_kernel"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = "ordinary_failed_step",
        });
        state.ExecutionStates["delay"] = Any.Pack(new DelayModuleState
        {
            Pending =
            {
                ["run-activity:delay_step"] = new PendingDelayState
                {
                    StepId = "delay_step",
                    CallbackId = "delay-step:run-activity:delay_step:envelope-alpha",
                    Input = "wait before retry",
                },
            },
        });

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    Success = false,
                },
                state));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ActivityFirstFailure.Availability.Should().Be("available");
        document.ActivityFirstFailure.StepId.Should().Be("ordinary_failed_step");
        document.ActivityFirstFailure.Message.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        document.ActivityFirstFailure.Message.Should().NotContain(opaqueCredential);
        document.ActivityWaiting.Availability.Should().Be("available");
        document.ActivityWaiting.WaitingKind.Should().Be("delay");
        document.ActivityWaiting.StepId.Should().Be("delay_step");
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldResolveLegacyDelayStepIdFromPendingKey()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-10T09:00:00+00:00")));
        var state = new WorkflowRunState
        {
            RunId = "run-legacy-delay",
            ScopeId = "scope-activity",
            Status = "running",
        };
        state.ExecutionStates["delay"] = Any.Pack(new DelayModuleState
        {
            Pending =
            {
                ["run-legacy-delay:delay_step"] = new PendingDelayState
                {
                    CallbackId = "delay-step:run-legacy-delay:delay_step:envelope-alpha",
                    Input = "legacy wait",
                },
            },
        });

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    Success = false,
                },
                state));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ActivityWaiting.Availability.Should().Be("available");
        document.ActivityWaiting.WaitingKind.Should().Be("delay");
        document.ActivityWaiting.StepId.Should().Be("delay_step");
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldMaterializeEligibleRecoveryCapability()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T03:00:00+00:00")));
        var state = new WorkflowRunState
        {
            RunId = "run-retry",
            Status = "failed",
            WorkflowYaml = CurrentStateWorkflowYaml("wf-retry"),
            RevisionId = "rev-source",
            DefinitionVersion = 17,
            FinalError = "tool timeout",
        };
        state.ExecutionStates["workflow_execution_kernel"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = "step-b",
            Variables =
            {
                ["input"] = "original input",
                ["step-a"] = "prior output",
            },
        });

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent { Success = false, Error = "tool timeout" },
                state));

        var capability = dispatcher.Upserts.Should().ContainSingle().Subject.RecoveryCapability;
        capability.WorkflowDefinitionRevisionId.Should().Be("rev-source");
        capability.WorkflowDefinitionVersion.Should().Be(17);
        capability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Eligible);
        capability.RetryFailedStep.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCodeReadModel.None);
        capability.RetryFailedStep.StartingStepId.Should().Be("step-b");
        capability.RetryFailedStep.ReusesPriorStepOutputs.Should().BeTrue();
        capability.RetryFailedStep.MayIncurModelOrToolCost.Should().BeTrue();
        capability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedActionReadModel.Retry);
        capability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Eligible);
        capability.RunAgain.StartingStepId.Should().Be("step-a");
        capability.RunAgain.ReusesPriorStepOutputs.Should().BeFalse();
        capability.RunAgain.MayIncurModelOrToolCost.Should().BeTrue();
        capability.RunAgain.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedActionReadModel.RunAgain);
    }

    [Theory]
    [InlineData(WorkflowRecoveryFailureKind.AuthorizationFailure, WorkflowRecoveryUnavailableReasonCodeReadModel.AuthorizationFailure, WorkflowRecoveryRecommendedActionReadModel.FixAccess)]
    [InlineData(WorkflowRecoveryFailureKind.ConfigurationFailure, WorkflowRecoveryUnavailableReasonCodeReadModel.ConfigurationFailure, WorkflowRecoveryRecommendedActionReadModel.ChangeConfiguration)]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldNotAdvertiseRetryForTypedBackendClassifiedFailures(
        WorkflowRecoveryFailureKind recoveryFailureKind,
        WorkflowRecoveryUnavailableReasonCodeReadModel expectedReason,
        WorkflowRecoveryRecommendedActionReadModel expectedAction)
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T03:10:00+00:00")));
        var state = new WorkflowRunState
        {
            RunId = "run-classified",
            Status = "failed",
            WorkflowYaml = CurrentStateWorkflowYaml("wf-classified"),
            FinalError = "diagnostic text without recovery control meaning",
            TerminalRecoveryFailureKind = recoveryFailureKind,
        };
        state.ExecutionStates["workflow_execution_kernel"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = "step-b",
        });

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    Success = false,
                    Error = "diagnostic text without recovery control meaning",
                    RecoveryFailureKind = recoveryFailureKind,
                },
                state));

        var capability = dispatcher.Upserts.Should().ContainSingle().Subject.RecoveryCapability;
        capability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Ineligible);
        capability.RetryFailedStep.UnavailableReasonCode.Should().Be(expectedReason);
        capability.RetryFailedStep.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        capability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(expectedAction);
        capability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Ineligible);
        capability.RunAgain.UnavailableReasonCode.Should().Be(expectedReason);
        capability.RunAgain.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(expectedAction);
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldExposeUnavailableRecoveryReasonsForMissingFacts()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T03:20:00+00:00")));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent { Success = false, Error = "boom" },
                new WorkflowRunState
                {
                    RunId = "run-missing-definition",
                    Status = "failed",
                    FinalError = "boom",
                }));

        var capability = dispatcher.Upserts.Should().ContainSingle().Subject.RecoveryCapability;
        capability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Unavailable);
        capability.RetryFailedStep.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCodeReadModel.MissingSourceFact);
        capability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedActionReadModel.EditWorkflow);
        capability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Unavailable);
        capability.RunAgain.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCodeReadModel.WorkflowDefinitionUnavailable);
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldExposeLegacyUnavailableWhenFailedStepWasNotMaterialized()
    {
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T03:30:00+00:00")));

        await projector.ProjectAsync(
            CreateContext(),
            WrapCommitted(
                new WorkflowCompletedEvent { Success = false, Error = "boom" },
                new WorkflowRunState
                {
                    RunId = "run-legacy-failed-step",
                    Status = "failed",
                    WorkflowYaml = CurrentStateWorkflowYaml("wf-legacy"),
                    FinalError = "boom",
                }));

        var capability = dispatcher.Upserts.Should().ContainSingle().Subject.RecoveryCapability;
        capability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Unavailable);
        capability.RetryFailedStep.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCodeReadModel.LegacyUnavailable);
        capability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedActionReadModel.TechnicalDetails);
        capability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibilityReadModel.Eligible);
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
        materialization.Nodes.Should().Contain(x =>
            x.NodeId == "actor:unknown:unknown:child-1" &&
            x.Properties["actorId"] == "child-1");

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
                x.ToNodeId == "actor:unknown:unknown:child-1")
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
                    new ProjectionGraphNode
                    {
                        NodeId = "run:node-1:cmd-1",
                        NodeType = WorkflowExecutionGraphConstants.RunNodeType,
                        Properties =
                        {
                            [WorkflowExecutionGraphConstants.RootActorIdPropertyKey] = "node-1",
                            [WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey] = "12",
                        },
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
        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.Nodes.Should().HaveCount(2);
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
        yield return ["timed_out", false];
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

    private static string CurrentStateWorkflowYaml(string name) =>
        $$"""
        name: {{name}}
        roles: []
        steps:
          - id: step-a
            type: transform
          - id: step-b
            type: transform
        """;

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
