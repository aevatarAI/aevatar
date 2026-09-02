using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class HumanInteractionMessageMapperTests
{
    [Fact]
    public void ToMessageContent_WhenTemplateSpecPresent_ShouldBuildChannelNeutralFallbackCard()
    {
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["title"] = "Deploy";
        template.TemplateVariable["run"] = "run-1";

        var content = HumanInteractionMessageMapper.ToMessageContent(new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-template",
            DeliveryTargetId = "agent-lark-1",
            InteractionTemplateSpec = template,
        });

        content.Text.Should().Be("Interaction notification template: tpl-1");
        content.Actions.Should().BeEmpty();
        content.Cards.Should().ContainSingle();
        var card = content.Cards[0];
        card.Title.Should().Be("Interaction notification");
        card.Text.Should().Be("Template ID: tpl-1");
        card.Fields.Should().Contain(field => field.Title == "run" && field.Text == "run-1");
        card.Fields.Should().Contain(field => field.Title == "title" && field.Text == "Deploy");
    }

    [Fact]
    public void ToMessageContent_WhenPayloadMissingOrAmbiguous_ShouldReject()
    {
        var empty = new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-template",
            DeliveryTargetId = "agent-lark-1",
        };
        var both = empty with
        {
            InteractionSpec = new InteractionSpec { Title = "Approve" },
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
