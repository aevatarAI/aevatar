using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionReadModelMapperTests
{
    [Theory]
    [InlineData("running", WorkflowRunCompletionStatus.Running)]
    [InlineData("completed", WorkflowRunCompletionStatus.Completed)]
    [InlineData("timed_out", WorkflowRunCompletionStatus.TimedOut)]
    [InlineData("failed", WorkflowRunCompletionStatus.Failed)]
    [InlineData("stopped", WorkflowRunCompletionStatus.Stopped)]
    [InlineData("not_found", WorkflowRunCompletionStatus.NotFound)]
    [InlineData("disabled", WorkflowRunCompletionStatus.Disabled)]
    [InlineData("awaiting_tool_approval", WorkflowRunCompletionStatus.AwaitingToolApproval)]
    [InlineData("waiting_for_signal", WorkflowRunCompletionStatus.WaitingForSignal)]
    [InlineData("unknown", WorkflowRunCompletionStatus.Unknown)]
    public void ToActorSnapshot_ShouldMapCurrentStateStatuses(
        string status,
        WorkflowRunCompletionStatus expected)
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-1",
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            Status = status,
            FinalOutput = "done",
            FinalError = "err",
            TerminalValueLifecycleFailureKind =
                WorkflowValueLifecycleFailureKind.ReleaseTargetMissing,
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2,
            DeadLetterError = "refund failed",
            UpdatedAt = DateTimeOffset.Parse("2026-03-17T08:00:00+00:00"),
        });

        snapshot.CompletionStatus.Should().Be(expected);
        snapshot.LastOutput.Should().Be("done");
        snapshot.LastError.Should().Be("err");
        snapshot.TerminalValueLifecycleFailureKind.Should().Be(
            WorkflowValueLifecycleFailureKind.ReleaseTargetMissing);
        snapshot.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        snapshot.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        snapshot.DeadLetterRemainingUncompensated.Should().Be(2);
        snapshot.DeadLetterError.Should().Be("refund failed");
    }

    [Fact]
    public void ToActorSnapshot_ShouldExposeTypedLineage()
    {
        var mapper = new WorkflowExecutionReadModelMapper();

        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-child-delta",
            RunId = "run-child-beta",
            Status = "running",
            Lineage = new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                RetryFork = new WorkflowRunRetryForkLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    SourceRunId = "run-source-gamma",
                    OriginalRunId = "run-original-alpha",
                    Attempt = 2,
                    StartAtStepId = "step-retry",
                },
                SubWorkflow = new WorkflowRunSubWorkflowLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    ParentRunId = "run-parent-alpha",
                    ParentActorId = "actor-parent-gamma",
                    ParentStepId = "step-call-child",
                    RootRunId = "run-root-omega",
                    Depth = 2,
                },
            },
        });

        snapshot.RunId.Should().Be("run-child-beta");
        snapshot.ActorId.Should().Be("actor-child-delta");
        snapshot.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        snapshot.Lineage.RetryFork.SourceRunId.Should().Be("run-source-gamma");
        snapshot.Lineage.RetryFork.OriginalRunId.Should().Be("run-original-alpha");
        snapshot.Lineage.RetryFork.StartAtStepId.Should().Be("step-retry");
        snapshot.Lineage.RetryFork.Attempt.Should().Be(2);
        snapshot.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        snapshot.Lineage.SubWorkflow.ParentRunId.Should().Be("run-parent-alpha");
        snapshot.Lineage.SubWorkflow.ParentActorId.Should().Be("actor-parent-gamma");
        snapshot.Lineage.SubWorkflow.ParentStepId.Should().Be("step-call-child");
        snapshot.Lineage.SubWorkflow.RootRunId.Should().Be("run-root-omega");
        snapshot.Lineage.SubWorkflow.Depth.Should().Be(2);
    }

    [Fact]
    public void ToActorSnapshot_WhenLineageMissing_ShouldReturnLegacyUnavailable()
    {
        var mapper = new WorkflowExecutionReadModelMapper();

        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-legacy-delta",
            RunId = "run-legacy-beta",
            Status = "completed",
        });

        snapshot.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.LegacyUnavailable);
        snapshot.Lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.LegacyUnavailable);
        snapshot.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.LegacyUnavailable);
        snapshot.Lineage.UnavailableReason.Should().Contain("legacy");
    }
}
