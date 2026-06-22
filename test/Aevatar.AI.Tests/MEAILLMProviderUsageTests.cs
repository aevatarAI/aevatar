using System.ClientModel.Primitives;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using FluentAssertions;
using Microsoft.Extensions.AI;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Aevatar.AI.Tests;

// 06-20-observatory-token-graph-ui (R2): the streaming provider used to drop token usage entirely —
// the switch over update.Contents never matched UsageContent and the terminal chunk carried no Usage,
// so every workflow run reported 0 tokens. These cover the two-part fix: (A) opt into streaming usage on
// the OpenAI request, (B) capture the emitted UsageContent and surface it on the terminal chunk.
public sealed class MEAILLMProviderUsageTests
{
    [Fact]
    public async Task ChatStreamAsync_ShouldEmitTokenUsage_FromStreamingUsageContent()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamWithUsage(
                ["hello"],
                new UsageDetails { InputTokenCount = 12, OutputTokenCount = 8, TotalTokenCount = 20 }),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        var last = chunks[^1];
        last.IsLast.Should().BeTrue();
        last.Usage.Should().NotBeNull();
        last.Usage!.PromptTokens.Should().Be(12);
        last.Usage.CompletionTokens.Should().Be(8);
        last.Usage.TotalTokens.Should().Be(20);
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldFallBackTotalToPromptPlusCompletion_WhenProviderOmitsTotal()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamWithUsage(
                ["hi"],
                new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12, TotalTokenCount = null }),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        chunks[^1].Usage.Should().NotBeNull();
        chunks[^1].Usage!.TotalTokens.Should().Be(42);
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldLeaveUsageNull_WhenProviderReturnsNoUsage()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamText(["only", " text"]),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        var last = chunks.Single(chunk => chunk.IsLast);
        last.Usage.Should().BeNull();
        string.Concat(chunks.Where(chunk => chunk.DeltaContent != null).Select(chunk => chunk.DeltaContent))
            .Should().Be("only text");
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldOptIntoStreamUsage_ButNonStreamingFallbackShouldNot()
    {
        ChatOptions? streamingOptions = null;
        ChatOptions? fallbackOptions = null;
        var fallbackInvoked = false;
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, options, _) =>
            {
                streamingOptions = options;
                return EmptyStream();
            },
            OnGetResponse = options =>
            {
                fallbackInvoked = true;
                fallbackOptions = options;
            },
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        // A Model on the request makes BuildOptions return non-null options for both paths so the
        // fallback assertion compares a real options object (not a trivially-null one).
        _ = await CollectAsync(provider, "gpt-test");

        // (A) the streaming request opts into usage via a raw ChatCompletionOptions patch.
        streamingOptions.Should().NotBeNull();
        streamingOptions!.RawRepresentationFactory.Should().NotBeNull();
        var rawOptions = streamingOptions.RawRepresentationFactory!(client);
        rawOptions.Should().NotBeNull();
        var json = ModelReaderWriter.Write(rawOptions!).ToString();
        json.Should().Contain("stream_options");
        json.Should().Contain("include_usage");

        // Scoping: the zero-chunk non-streaming fallback runs and its options must NOT carry the
        // stream-usage opt-in (OpenAI rejects stream_options without stream=true).
        fallbackInvoked.Should().BeTrue();
        fallbackOptions.Should().NotBeNull();
        fallbackOptions!.RawRepresentationFactory.Should().BeNull();
    }

    private static async Task<List<LLMStreamChunk>> CollectAsync(MEAILLMProvider provider, string? model = null)
    {
        var request = new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
            Model = model,
        };
        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in provider.ChatStreamAsync(request))
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamText(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithUsage(
        IEnumerable<string> parts,
        UsageDetails usage)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class UsageCapturingChatClient : IChatClient
    {
        public Func<IEnumerable<MeaiChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? OnGetStreamingResponse { get; init; }

        public Action<ChatOptions?>? OnGetResponse { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            OnGetResponse?.Invoke(options);
            return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            OnGetStreamingResponse?.Invoke(messages, options, cancellationToken) ?? EmptyStream();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
