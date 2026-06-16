using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.LLMProviders.MEAI;
using FluentAssertions;
using Microsoft.Extensions.AI;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Aevatar.AI.Tests;

public sealed class ChatToolCallTranscriptTests
{
    [Fact]
    public void ChatHistory_ShouldDropOrphanToolResultsAfterTruncation()
    {
        var history = new ChatHistory { MaxMessages = 2 };
        history.AddRange([
            new AevatarChatMessage { Role = "user", Content = "older" },
            new AevatarChatMessage { Role = "assistant", ToolCalls = [ToolCall("call-1", "lookup")] },
            new AevatarChatMessage { Role = "tool", ToolCallId = "call-1", Content = "{\"ok\":true}" },
            new AevatarChatMessage { Role = "user", Content = "next" },
        ]);

        history.Messages.Should().HaveCount(1);
        history.Messages[0].Role.Should().Be("user");
        history.Messages[0].Content.Should().Be("next");
    }

    [Fact]
    public void ChatHistory_BuildMessages_ShouldHideImportedOrphanToolResults()
    {
        var history = new ChatHistory();
        history.Import([
            new SerializableMessage { Role = "tool", ToolCallId = "missing-call", Content = "orphan" },
            new SerializableMessage { Role = "user", Content = "next" },
        ]);

        var messages = history.BuildMessages("system");

        messages.Select(message => message.Role).Should().Equal("system", "user");
        messages.Should().NotContain(message => message.Role == "tool");
    }

    [Fact]
    public void TranscriptSanitizer_ShouldPreserveOnlyCompleteToolCallGroups()
    {
        var sanitized = ChatMessageToolCallTranscript.WithoutInvalidToolCallPairs([
            new AevatarChatMessage { Role = "tool", ToolCallId = "orphan", Content = "orphan" },
            new AevatarChatMessage { Role = "assistant", ToolCalls = [ToolCall("complete", "lookup")] },
            new AevatarChatMessage { Role = "tool", ToolCallId = "complete", Content = "ok" },
            new AevatarChatMessage { Role = "assistant", ToolCalls = [ToolCall("missing", "lookup")] },
            new AevatarChatMessage { Role = "user", Content = "next" },
        ]);

        sanitized.Select(message => message.Role).Should().Equal("assistant", "tool", "user");
        sanitized[0].ToolCalls![0].Id.Should().Be("complete");
        sanitized[1].ToolCallId.Should().Be("complete");
    }

    [Fact]
    public async Task MeaiProvider_ShouldNotForwardOrphanToolResult()
    {
        IReadOnlyList<MeaiChatMessage>? capturedMessages = null;
        var client = new CapturingChatClient
        {
            OnGetStreamingResponse = (messages, _, _) =>
            {
                capturedMessages = messages.ToList();
                return Stream(["ok"]);
            },
        };

        var provider = new MEAILLMProvider("meai-tool-transcript", client);
        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages =
            [
                new AevatarChatMessage { Role = "user", Content = "hi" },
                new AevatarChatMessage { Role = "tool", ToolCallId = "fc_orphan", Content = "{\"error\":\"failed\"}" },
                new AevatarChatMessage { Role = "user", Content = "continue" },
            ],
        }))
        {
        }

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Select(message => message.Role).Should().Equal(ChatRole.User, ChatRole.User);
    }

    private static ToolCall ToolCall(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            ArgumentsJson = "{}",
        };

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public Func<IEnumerable<MeaiChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? OnGetStreamingResponse { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (OnGetStreamingResponse != null)
                return OnGetStreamingResponse(messages, options, cancellationToken);

            return EmptyStream(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
