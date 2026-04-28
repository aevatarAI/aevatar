using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowTextRoutingTests
{
    [Fact]
    public void TryBuildWorkflowResumeCommand_ShouldBuildApproveCommand_FromText()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = "user",
            Text = "/approve actor_id=run-actor-1 run_id=run-1 step_id=approval-1 edited_content=\"final draft\"",
            MessageId = "msg-1",
            ChatType = "p2p",
        };

        var matched = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.Approved.Should().BeTrue();
        command.ActorId.Should().Be("run-actor-1");
        command.RunId.Should().Be("run-1");
        command.StepId.Should().Be("approval-1");
        command.UserInput.Should().Be("final draft");
        command.EditedContent.Should().Be("final draft");
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_ShouldBuildRejectCommand_FromText()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = "user",
            Text = "/reject actor_id=run-actor-1 run_id=run-1 step_id=approval-1 feedback=\"Need stronger hook\"",
            MessageId = "msg-2",
            ChatType = "p2p",
        };

        var matched = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.Approved.Should().BeFalse();
        command.UserInput.Should().Be("Need stronger hook");
        command.Feedback.Should().Be("Need stronger hook");
        command.EditedContent.Should().BeNull();
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_ShouldBuildSubmitCommand_ForHumanInput()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = "user",
            Text = "/submit actor_id=run-actor-1 run_id=run-1 step_id=input-1 user_input=\"my response\"",
            MessageId = "msg-3",
            ChatType = "p2p",
        };

        var matched = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.Approved.Should().BeTrue();
        command.UserInput.Should().Be("my response");
        command.EditedContent.Should().BeNull();
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_ShouldIgnoreNonWorkflowText()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = "user",
            Text = "hello there",
            ChatType = "p2p",
        };

        var matched = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeFalse();
        command.Should().BeNull();
    }
}
