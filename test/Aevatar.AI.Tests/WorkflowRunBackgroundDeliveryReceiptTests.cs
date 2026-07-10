using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class WorkflowRunBackgroundDeliveryReceiptTests
{
    [Fact]
    public void AgentToolReceipt_ShouldCarryWorkflowRunBackgroundDeliveryReceipt()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "call-1",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            ResultJson = "{}",
            WorkflowRunDelivery = new WorkflowRunBackgroundDeliveryReceipt
            {
                DeliveryActorId = "workflow-run-delivery:actor-1:command-1",
                WorkflowActorId = "workflow-actor",
                WorkflowRunId = "workflow-run-1",
                WorkflowCommandId = "workflow-command-1",
                WorkflowCorrelationId = "workflow-correlation-1",
                StreamTopic = "aevatar://actors/workflow-actor/runs/workflow-command-1",
                ChannelPlatform = "lark",
                ReplyMessageId = "reply-message-1",
                PlatformMessageId = "platform-message-1",
                RegistrationScopeId = "registration-scope-1",
            },
        };

        var roundTripped = AgentToolReceipt.Parser.ParseFrom(receipt.ToByteArray());

        roundTripped.WorkflowRunDelivery.DeliveryActorId.Should()
            .Be("workflow-run-delivery:actor-1:command-1");
        roundTripped.WorkflowRunDelivery.WorkflowActorId.Should().Be("workflow-actor");
        roundTripped.WorkflowRunDelivery.WorkflowRunId.Should().Be("workflow-run-1");
        roundTripped.WorkflowRunDelivery.WorkflowCommandId.Should().Be("workflow-command-1");
        roundTripped.WorkflowRunDelivery.WorkflowCorrelationId.Should().Be("workflow-correlation-1");
        roundTripped.WorkflowRunDelivery.StreamTopic.Should()
            .Be("aevatar://actors/workflow-actor/runs/workflow-command-1");
        roundTripped.WorkflowRunDelivery.ChannelPlatform.Should().Be("lark");
        roundTripped.WorkflowRunDelivery.ReplyMessageId.Should().Be("reply-message-1");
        roundTripped.WorkflowRunDelivery.PlatformMessageId.Should().Be("platform-message-1");
        roundTripped.WorkflowRunDelivery.RegistrationScopeId.Should().Be("registration-scope-1");
        // The receipt no longer carries a credential handle field at all.
        WorkflowRunBackgroundDeliveryReceipt.Descriptor
            .FindFieldByName("durable_reply_credential_ref").Should().BeNull();
    }

    [Fact]
    public void AgentToolReceiptDescriptor_ShouldExposeWorkflowRunDeliveryAtStableTag()
    {
        AgentToolReceipt.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((16, "workflow_run_delivery"));

        AiMessagesReflection.Descriptor.MessageTypes.Should()
            .Contain(x => x.Name == nameof(WorkflowRunBackgroundDeliveryReceipt));
    }
}
