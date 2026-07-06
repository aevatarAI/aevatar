using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class HumanInteractionMessageMapperTests
{
    [Fact]
    public void ToMessageContent_WhenApprovalSpecPresent_ShouldAttachWorkflowResumeIdentityToApprovalActions()
    {
        var content = HumanInteractionMessageMapper.ToMessageContent(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "step-1",
                DeliveryTargetId = "agent-1",
                InteractionSpec = new InteractionSpec
                {
                    Title = "Approval required",
                    Body = "Approve deployment?",
                    Actions =
                    {
                        new InteractionAction
                        {
                            Kind = InteractionActionKind.FormSubmit,
                            ActionId = "approve",
                            Label = "Approve",
                            ApprovalDecision = InteractionApprovalDecision.Approve,
                        },
                        new InteractionAction
                        {
                            Kind = InteractionActionKind.FormSubmit,
                            ActionId = "reject",
                            Label = "Reject",
                            ApprovalDecision = InteractionApprovalDecision.Reject,
                        },
                    },
                },
            });

        content.Text.Should().Be("Approval required\nApprove deployment?");
        content.Actions.Should().HaveCount(2);
        content.Actions[0].WorkflowResume.ActorId.Should().Be("workflow-actor-1");
        content.Actions[0].WorkflowResume.RunId.Should().Be("run-1");
        content.Actions[0].WorkflowResume.StepId.Should().Be("step-1");
        content.Actions[0].WorkflowResume.Approved.Should().BeTrue();
        content.Actions[1].WorkflowResume.ActorId.Should().Be("workflow-actor-1");
        content.Actions[1].WorkflowResume.RunId.Should().Be("run-1");
        content.Actions[1].WorkflowResume.StepId.Should().Be("step-1");
        content.Actions[1].WorkflowResume.Approved.Should().BeFalse();
    }

    [Fact]
    public void ToMessageContent_WhenTemplateSpecPresent_ShouldProduceDeterministicFallback()
    {
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["zeta"] = "last";
        template.TemplateVariable["alpha"] = "first";

        var content = HumanInteractionMessageMapper.ToMessageContent(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "step-1",
                DeliveryTargetId = "agent-1",
                InteractionTemplateSpec = template,
            });

        content.Text.Should().Be("Interaction notification template: tpl-1");
        content.Cards.Should().ContainSingle();
        content.Cards[0].Fields.Select(field => field.Title)
            .Should().Equal("alpha", "zeta");
    }

    [Fact]
    public void ToMessageContent_ShouldRejectMissingOrAmbiguousPayloads()
    {
        var empty = new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "step-1",
            DeliveryTargetId = "agent-1",
        };
        var both = empty with
        {
            InteractionSpec = new InteractionSpec { Title = "Approval" },
            InteractionTemplateSpec = new InteractionTemplateSpec { TemplateId = "tpl-1" },
        };

        Action emptyAct = () => HumanInteractionMessageMapper.ToMessageContent(empty);
        Action bothAct = () => HumanInteractionMessageMapper.ToMessageContent(both);

        emptyAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one typed payload*");
        bothAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one typed payload*");
    }
}
