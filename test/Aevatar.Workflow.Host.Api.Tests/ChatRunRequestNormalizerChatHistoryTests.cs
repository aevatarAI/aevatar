using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatRunRequestNormalizerChatHistoryTests
{
    [Fact]
    public void HttpChatInput_ShouldRejectLegacyChatHistoryWriteIntent()
    {
        var act = () => JsonSerializer.Deserialize<HttpChatInput>(
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
            ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void HttpChatInput_ShouldRejectBodyScopeId()
    {
        var act = () => JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "scopeId": "scope-from-body"
            }
            """,
            ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Normalize_ShouldMapConversationNullToCreateIntent()
    {
        var input = JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "conversation": {
                "conversationId": null
              }
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            trustedScopeId: "trusted-scope");

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("trusted-scope");
        result.Request.ChatConversation.Should().BeEquivalentTo(
            WorkflowChatConversationIntent.Create());
        result.Request.ChatHistory.Should().BeNull();
    }

    [Fact]
    public void Normalize_ShouldMapConversationIdToContinueIntent()
    {
        var input = JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "conversation": {
                "conversationId": " conversation-existing "
              }
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            trustedScopeId: "trusted-scope");

        result.Succeeded.Should().BeTrue();
        result.Request!.ChatConversation.Should().BeEquivalentTo(
            WorkflowChatConversationIntent.Continue("conversation-existing"));
        result.Request.ChatHistory.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldRejectBlankConversationId(string conversationId)
    {
        var input = new HttpChatInput
        {
            Prompt = "assistant prompt",
            Conversation = new ChatConversationInput
            {
                ConversationId = conversationId,
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            trustedScopeId: "trusted-scope");

        result.Succeeded.Should().BeFalse();
        result.Request.Should().BeNull();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidConversationId);
    }
}
