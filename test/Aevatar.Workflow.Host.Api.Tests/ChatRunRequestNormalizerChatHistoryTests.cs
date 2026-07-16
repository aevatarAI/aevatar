using System.Text.Json;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatRunRequestNormalizerChatHistoryTests
{
    [Fact]
    public void Normalize_ShouldTrimAndCarryChatHistoryWriteIntent()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "chatHistory": {
                "conversationId": " conversation-a ",
                "turnId": " turn-a ",
                "userText": " original user text "
              }
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        input.ChatHistory.Should().NotBeNull();
        input.ChatHistory!.ConversationId.Should().Be(" conversation-a ");
        input.ChatHistory.TurnId.Should().Be(" turn-a ");
        input.ChatHistory.UserText.Should().Be(" original user text ");

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ChatHistory.Should().NotBeNull();
        result.Request.ChatHistory!.ConversationId.Should().Be("conversation-a");
        result.Request.ChatHistory.TurnId.Should().Be("turn-a");
        result.Request.ChatHistory.UserText.Should().Be("original user text");
    }

    [Theory]
    [InlineData(null, " turn-a ", " original user text ")]
    [InlineData(" conversation-a ", null, " original user text ")]
    [InlineData(" conversation-a ", " turn-a ", null)]
    [InlineData("   ", " turn-a ", " original user text ")]
    [InlineData(" conversation-a ", "   ", " original user text ")]
    [InlineData(" conversation-a ", " turn-a ", "   ")]
    public void Normalize_ShouldDropChatHistoryWriteIntent_WhenAnyRequiredFieldIsBlank(
        string? conversationId,
        string? turnId,
        string? userText)
    {
        var input = new ChatInput
        {
            Prompt = "assistant prompt",
            ChatHistory = new ChatHistoryWriteIntentInput
            {
                ConversationId = conversationId,
                TurnId = turnId,
                UserText = userText,
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ChatHistory.Should().BeNull();
    }
}
