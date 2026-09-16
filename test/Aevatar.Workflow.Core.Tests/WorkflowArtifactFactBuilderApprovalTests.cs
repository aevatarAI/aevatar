using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowArtifactFactBuilderApprovalTests
{
    [Fact]
    public void TryBuild_ShouldPreserveToolApprovalResumeRejection()
    {
        var rejection = new WorkflowToolApprovalResumeRejectedEvent
        {
            RunId = "run-1",
            StepId = "tool-step",
            SubmittedApproval = new WorkflowToolApprovalResume
            {
                ExecutionId = "exec-1",
                ToolCallId = "call-1",
                ApprovalRequestId = "approval-stale",
            },
            Reason = WorkflowToolApprovalResumeRejectionReason.IdentityMismatch,
        };

        var ok = WorkflowArtifactFactBuilder.TryBuild(
            new EventEnvelope
            {
                Id = "env-resume-rejected",
                Payload = Any.Pack(rejection),
            },
            "workflow-run",
            "run-1",
            out var artifactFact);

        ok.Should().BeTrue();
        artifactFact.Should().BeOfType<WorkflowToolApprovalResumeRejectedEvent>()
            .Which.Should().BeEquivalentTo(rejection);
    }
}
