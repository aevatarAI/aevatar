using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatRunModelsTests
{
    [Fact]
    public void ConversationIntentFactories_ShouldCreateExpectedIntent()
    {
        WorkflowChatConversationIntent.None().Should().BeEquivalentTo(
            new WorkflowChatConversationIntent(WorkflowChatConversationIntentKind.None));
        WorkflowChatConversationIntent.Create().Should().BeEquivalentTo(
            new WorkflowChatConversationIntent(WorkflowChatConversationIntentKind.Create));
        WorkflowChatConversationIntent.Continue("conversation-a").Should().BeEquivalentTo(
            new WorkflowChatConversationIntent(
                WorkflowChatConversationIntentKind.Continue,
                "conversation-a",
                null));
        WorkflowChatConversationIntent.Continue("conversation-a", minimumStateVersion: 7).Should().BeEquivalentTo(
            new WorkflowChatConversationIntent(
                WorkflowChatConversationIntentKind.Continue,
                "conversation-a",
                7));
    }

    [Fact]
    public void InteractionAcceptedReceipt_ShouldAllowRunReceiptImplicitConversion()
    {
        var runReceipt = new WorkflowChatRunAcceptedReceipt(
            "actor-1",
            "direct",
            "cmd-1",
            "corr-1");

        WorkflowChatInteractionAcceptedReceipt interactionReceipt = runReceipt;

        interactionReceipt.Run.Should().BeSameAs(runReceipt);
        interactionReceipt.ChatContext.Should().BeNull();
    }
}
