using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowSuspendedEventHumanInteractionContractTests
{
    [Fact]
    public void WorkflowSuspendedEvent_ShouldRoundtripHumanInteractionTimeoutDecisionAndCallback()
    {
        WorkflowSuspendedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 14 && field.Name == "timeout_default_decision");
        WorkflowSuspendedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 15 && field.Name == "callback");

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = "run-hitl",
            StepId = "approval-hitl",
            SuspensionType = "human_approval",
            DeliveryTargetId = "agent-hitl",
            TimeoutDefaultDecision = WorkflowHumanApprovalTimeoutDefaultDecision.Reject,
            Callback = new WorkflowHumanInteractionCallback
            {
                Kind = "workflow_resume",
                ActorId = "workflow-actor-hitl",
                RunId = "run-hitl",
                StepId = "approval-hitl",
            },
        };

        var parsed = WorkflowSuspendedEvent.Parser.ParseFrom(suspended.ToByteArray());

        parsed.TimeoutDefaultDecision.Should().Be(WorkflowHumanApprovalTimeoutDefaultDecision.Reject);
        parsed.Callback.Kind.Should().Be("workflow_resume");
        parsed.Callback.ActorId.Should().Be("workflow-actor-hitl");
        parsed.Callback.RunId.Should().Be("run-hitl");
        parsed.Callback.StepId.Should().Be("approval-hitl");
    }
}
