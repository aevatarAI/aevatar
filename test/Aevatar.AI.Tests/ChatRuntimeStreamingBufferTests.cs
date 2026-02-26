using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatRuntimeStreamingBufferTests
{
    [Fact]
    public async Task ChatStreamAsync_WhenBufferIsBounded_ShouldStillStreamAllChunks()
    {
        var provider = new StreamingProvider(["A", "B", "C", "D"]);
        var runtime = CreateRuntime(provider, streamBufferCapacity: 1);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("ABCD");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderReturnsToolCallDelta_ShouldSurfaceStructuredChunks()
    {
        var provider = new StreamingProvider(["done"], streamToolCall: new ToolCall
        {
            Id = "tc-1",
            Name = "search",
            ArgumentsJson = "{\"q\":\"aevatar\"}",
        });
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaToolCall != null);
        var toolCall = chunks.First(x => x.DeltaToolCall != null).DeltaToolCall!;
        toolCall.Id.Should().Be("tc-1");
        toolCall.Name.Should().Be("search");
        toolCall.ArgumentsJson.Should().Contain("aevatar");
    }

    [Fact]
    public void Constructor_WhenStreamBufferCapacityIsInvalid_ShouldThrow()
    {
        var provider = new StreamingProvider([]);

        var act = () => CreateRuntime(provider, streamBufferCapacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ChatRuntime CreateRuntime(ILLMProvider provider, int streamBufferCapacity)
    {
        var history = new ChatHistory();
        var toolLoop = new ToolCallLoop(new ToolManager());

        return new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: null,
            requestBuilder: () => new LLMRequest { Messages = [] },
            streamBufferCapacity: streamBufferCapacity);
    }

    private sealed class StreamingProvider(IReadOnlyList<string> chunks, ToolCall? streamToolCall = null) : ILLMProvider
    {
        public string Name => "streaming-provider";
        public int StreamCallCount { get; private set; }

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse { Content = string.Concat(chunks) });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            StreamCallCount++;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }

            if (streamToolCall != null)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = streamToolCall,
                };
            }
        }
    }
}
