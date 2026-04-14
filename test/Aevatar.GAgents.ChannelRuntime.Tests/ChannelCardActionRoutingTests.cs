using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCardActionRoutingTests
{
    [Fact]
    public void TryBuildWorkflowResumeCommand_should_build_command_for_complete_card_action()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = string.Empty,
            Text = "{}",
            MessageId = "evt_card_1",
            ChatType = "card_action",
            Extra = new Dictionary<string, string>
            {
                ["actor_id"] = "run-actor-1",
                ["run_id"] = "run-1",
                ["step_id"] = "approval-1",
                ["approved"] = "false",
                ["user_input"] = "need edits",
            },
        };

        var matched = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.ActorId.Should().Be("run-actor-1");
        command.RunId.Should().Be("run-1");
        command.StepId.Should().Be("approval-1");
        command.CommandId.Should().Be("evt_card_1");
        command.Approved.Should().BeFalse();
        command.UserInput.Should().Be("need edits");
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.platform", "lark"));
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.conversation_id", "oc_chat_1"));
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.message_id", "evt_card_1"));
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_should_require_actor_run_and_step_ids()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = string.Empty,
            Text = "{}",
            ChatType = "card_action",
            Extra = new Dictionary<string, string>
            {
                ["run_id"] = "run-1",
                ["step_id"] = "approval-1",
            },
        };

        var matched = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeFalse();
        command.Should().BeNull();
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_should_ignore_non_card_messages()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = string.Empty,
            Text = "hello",
            ChatType = "p2p",
        };

        var matched = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeFalse();
        command.Should().BeNull();
    }
}
