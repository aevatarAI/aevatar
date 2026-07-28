using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatRunRequestNormalizerChatHistoryTests
{
    [Fact]
    public void WorkflowChatRunRequest_ShouldNotExposeLegacyChatHistory()
    {
        typeof(WorkflowChatRunRequest)
            .GetProperty("ChatHistory")
            .Should()
            .BeNull();
        typeof(ChatInput)
            .GetProperty("ChatHistory")
            .Should()
            .BeNull();
        typeof(HttpChatInput)
            .GetProperty("ChatHistory")
            .Should()
            .BeNull();
    }

    [Fact]
    public void ChatInput_ShouldRejectLegacyChatHistoryWriteIntent()
    {
        var act = () => JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "chatHistory": "legacy payload should be ignored"
            }
            """,
            ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

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
    public void HttpChatInput_ShouldIgnoreBodyScopeId()
    {
        var input = JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "scopeId": "scope-from-body"
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            trustedScopeId: "trusted-scope");

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("trusted-scope");
    }

    [Fact]
    public void Normalize_ShouldMapConversationNullToCreateIntent()
    {
        var input = JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "commandId": " create-command-1 ",
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
        result.Request.CommandIdSeed.Should().Be("create-command-1");
        result.Request.ChatConversation.Should().BeEquivalentTo(
            WorkflowChatConversationIntent.Create());
    }

    [Fact]
    public void Normalize_ShouldMapHttpCommandIdToTrustedCommandSeed()
    {
        var input = JsonSerializer.Deserialize<HttpChatInput>(
            """
            {
              "prompt": "assistant prompt",
              "commandId": " create-command-stable ",
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
        result.Request!.CommandIdSeed.Should().Be("create-command-stable");
    }

    [Fact]
    public void Normalize_ShouldReturnUnavailable_WhenContinuationStateVersionIsMissing()
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

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ChatHistoryReservationUnavailable);
    }

    [Fact]
    public void Normalize_ShouldMapConversationMinimumStateVersionToContinueIntent()
    {
        var input = new HttpChatInput
        {
            Prompt = "team01",
            Conversation = new ChatConversationInput
            {
                ConversationId = " conversation-alpha ",
                MinimumStateVersion = 7,
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ChatConversation.Should().BeEquivalentTo(
            WorkflowChatConversationIntent.Continue("conversation-alpha", minimumStateVersion: 7));
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
