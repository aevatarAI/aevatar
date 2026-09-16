using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatStreamContentAggregatorTests
{
    [Fact]
    public async Task AggregateResponseAsync_ShouldPreserveTextReasoningContentPartsUsageAndFinishReason()
    {
        var imagePart = ContentPart.ImageUriPart("https://example.test/image.png");
        var provider = new StubProvider([
            new LLMStreamChunk { DeltaContent = "hel" },
            new LLMStreamChunk { DeltaReasoningContent = "think " },
            new LLMStreamChunk { DeltaContent = "lo" },
            new LLMStreamChunk { DeltaContentPart = imagePart },
            new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(3, 5, 8),
                FinishReason = "stop",
            },
        ]);

        var response = await ChatStreamContentAggregator.AggregateResponseAsync(
            provider,
            new LLMRequest { Messages = [] });

        response.Content.Should().Be("hello");
        response.ReasoningContent.Should().Be("think ");
        response.ContentParts.Should().ContainSingle().Which.Should().BeSameAs(imagePart);
        response.Usage.Should().BeEquivalentTo(new TokenUsage(3, 5, 8));
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task AggregateResponseAsync_ShouldReconstructStreamedToolCallDeltas()
    {
        var provider = new StubProvider([
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall { Id = "call-1", Name = "lookup", ArgumentsJson = "{\"q\":" },
            },
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall { Id = "call-1", Name = string.Empty, ArgumentsJson = "\"aevatar\"}" },
            },
            new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" },
        ]);

        var response = await ChatStreamContentAggregator.AggregateResponseAsync(
            provider,
            new LLMRequest { Messages = [] });

        response.ToolCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(new ToolCall
        {
            Id = "call-1",
            Name = "lookup",
            ArgumentsJson = "{\"q\":\"aevatar\"}",
        });
        response.FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public async Task AggregateResponseAsync_ShouldReturnNullContentValues_WhenStreamHasNoContent()
    {
        var provider = new StubProvider([
            new LLMStreamChunk { Usage = new TokenUsage(1, 0, 1) },
            new LLMStreamChunk { IsLast = true, FinishReason = "stop" },
        ]);

        var response = await ChatStreamContentAggregator.AggregateResponseAsync(
            provider,
            new LLMRequest { Messages = [] });

        response.Content.Should().BeNull();
        response.ReasoningContent.Should().BeNull();
        response.ContentParts.Should().BeNull();
        response.ToolCalls.Should().BeNull();
        response.Usage.Should().BeEquivalentTo(new TokenUsage(1, 0, 1));
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public void StreamingToolCallAccumulator_ShouldNamespaceAnonymousIdsByRoundCallId()
    {
        var firstRound = new StreamingToolCallAccumulator("request-1:round:0");
        firstRound.TrackDelta(new ToolCall { Id = string.Empty, Name = "submit_record", ArgumentsJson = "{}" });
        var secondRound = new StreamingToolCallAccumulator("request-1:round:1");
        secondRound.TrackDelta(new ToolCall { Id = string.Empty, Name = "submit_record", ArgumentsJson = "{}" });

        var firstId = firstRound.BuildToolCalls().Should().ContainSingle().Subject.Id;
        var secondId = secondRound.BuildToolCalls().Should().ContainSingle().Subject.Id;
        firstId.Should().Be("request-1:round:0:stream-tool-call-1");
        secondId.Should().Be("request-1:round:1:stream-tool-call-1");
        secondId.Should().NotBe(firstId);
    }

    private sealed class StubProvider(IReadOnlyList<LLMStreamChunk> chunks) : ILLMProvider
    {
        public string Name => "stub";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }
}
