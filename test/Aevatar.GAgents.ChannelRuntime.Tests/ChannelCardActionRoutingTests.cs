using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
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
        command.EditedContent.Should().BeNull();
        command.Feedback.Should().Be("need edits");
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.platform", "lark"));
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.conversation_id", "oc_chat_1"));
        command.Metadata.Should().Contain(new KeyValuePair<string, string>("channel.message_id", "evt_card_1"));
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_should_prefer_edited_content_when_approved()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = string.Empty,
            Text = "{}",
            MessageId = "evt_card_approved_1",
            ChatType = "card_action",
            Extra = new Dictionary<string, string>
            {
                ["actor_id"] = "run-actor-1",
                ["run_id"] = "run-1",
                ["step_id"] = "approval-1",
                ["approved"] = "true",
                ["edited_content"] = "Rewritten final draft",
                ["user_input"] = "minor note",
            },
        };

        var matched = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.Approved.Should().BeTrue();
        command.UserInput.Should().Be("Rewritten final draft");
        command.EditedContent.Should().Be("Rewritten final draft");
        command.Feedback.Should().Be("minor note");
    }

    [Fact]
    public void TryBuildWorkflowResumeCommand_should_prefer_feedback_when_rejected()
    {
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_user_1",
            SenderName = string.Empty,
            Text = "{}",
            MessageId = "evt_card_rejected_1",
            ChatType = "card_action",
            Extra = new Dictionary<string, string>
            {
                ["actor_id"] = "run-actor-1",
                ["run_id"] = "run-1",
                ["step_id"] = "approval-1",
                ["approved"] = "false",
                ["edited_content"] = "Edited but not accepted",
                ["user_input"] = "Need stronger hook",
            },
        };

        var matched = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command);

        matched.Should().BeTrue();
        command.Should().NotBeNull();
        command!.Approved.Should().BeFalse();
        command.UserInput.Should().Be("Need stronger hook");
        command.EditedContent.Should().Be("Edited but not accepted");
        command.Feedback.Should().Be("Need stronger hook");
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
