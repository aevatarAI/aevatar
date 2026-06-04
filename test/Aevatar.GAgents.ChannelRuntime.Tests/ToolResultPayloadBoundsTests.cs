using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

// Defense-in-depth complement to the Kafka transport fix (commit f2c2319e7).
// AgentRunReplyGenerationExecutor.BuildToolStepContinuationAsync packs raw
// tool-result messages into AgentRunNextToolStepRequestedEvent, which is then
// serialized into an EventEnvelope and produced to Kafka. A run that aggregates
// large multi-source output can exceed max.message.bytes and fail the whole run
// with an opaque ProduceException. These tests pin the executor-boundary bound
// that truncates oversized tool output with a visible marker so the run still
// completes with a degraded-but-useful reply.
public sealed class ToolResultPayloadBoundsTests
{
    [Fact]
    public void BoundResultMessages_KeepsSmallMessageUnchanged()
    {
        var message = ToolResultMessage("call-1", "small result");
        var messages = new List<AgentRunChatMessage> { message };

        ToolResultPayloadBounds.BoundResultMessages(messages, maxMessageBytes: 4096, maxTotalBytes: 16384);

        messages.Should().ContainSingle();
        messages[0].Should().BeSameAs(message);
        messages[0].Content.Should().Be("small result");
    }

    [Fact]
    public void BoundResultMessages_TruncatesOversizedContentWithVisibleMarker()
    {
        const int cap = 8 * 1024;
        var huge = new string('x', 64 * 1024);
        var messages = new List<AgentRunChatMessage> { ToolResultMessage("call-1", huge) };

        ToolResultPayloadBounds.BoundResultMessages(messages, maxMessageBytes: cap, maxTotalBytes: int.MaxValue);

        messages[0].CalculateSize().Should().BeLessThanOrEqualTo(cap);
        messages[0].ToolCallId.Should().Be("call-1");
        messages[0].Content.Should().StartWith("xxxx");
        messages[0].Content.Should().Contain("truncated");
    }

    [Fact]
    public void BoundResultMessages_DropsOversizedAttachmentsWithMarker()
    {
        const int cap = 8 * 1024;
        var bigBase64 = new string('A', 64 * 1024);
        var message = ToolResultMessage("call-1", "see image");
        message.ContentParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            MediaType = "image/png",
            DataBase64 = bigBase64,
        });
        var messages = new List<AgentRunChatMessage> { message };

        ToolResultPayloadBounds.BoundResultMessages(messages, maxMessageBytes: cap, maxTotalBytes: int.MaxValue);

        messages[0].CalculateSize().Should().BeLessThanOrEqualTo(cap);
        messages[0].ContentParts.Should().BeEmpty();
        messages[0].Content.Should().Contain("attachment");
    }

    [Fact]
    public void BoundResultMessages_EnforcesTotalBudgetButKeepsEveryToolResult()
    {
        const int perMessage = 16 * 1024;
        const int total = 24 * 1024;
        var messages = new List<AgentRunChatMessage>
        {
            ToolResultMessage("call-1", new string('a', 32 * 1024)),
            ToolResultMessage("call-2", new string('b', 32 * 1024)),
            ToolResultMessage("call-3", new string('c', 32 * 1024)),
        };

        ToolResultPayloadBounds.BoundResultMessages(messages, perMessage, total);

        // Every tool call must still receive a result: the next LLM step requires
        // one tool result per tool_call_id, so messages are truncated, never dropped.
        messages.Select(m => m.ToolCallId).Should().Equal("call-1", "call-2", "call-3");
        messages.Should().OnlyContain(m => m.CalculateSize() <= perMessage);
        messages.Sum(m => (long)m.CalculateSize())
            .Should().BeLessThanOrEqualTo(total + (messages.Count * 1024L));
    }

    [Fact]
    public void BoundResultMessages_PreservesValidUtf8WhenTruncating()
    {
        const int cap = 4 * 1024;
        // Multi-byte characters at the truncation boundary must not be split.
        var multibyte = string.Concat(Enumerable.Repeat("日本語", 4096));
        var messages = new List<AgentRunChatMessage> { ToolResultMessage("call-1", multibyte) };

        ToolResultPayloadBounds.BoundResultMessages(messages, maxMessageBytes: cap, maxTotalBytes: int.MaxValue);

        messages[0].CalculateSize().Should().BeLessThanOrEqualTo(cap);
        // Round-trip through UTF-8 must be lossless (no replacement chars from a split sequence).
        var bytes = Encoding.UTF8.GetBytes(messages[0].Content);
        Encoding.UTF8.GetString(bytes).Should().Be(messages[0].Content);
        messages[0].Content.Should().NotContain("�");
    }

    [Fact]
    public void BoundResultMessages_HonoursCapSmallerThanTruncationMarker()
    {
        // A cap below even the marker length must still produce a message within
        // the byte bound (the hard-cap fallback), never overshoot it.
        const int cap = 64;
        var messages = new List<AgentRunChatMessage>
        {
            ToolResultMessage("call-1", new string('x', 16 * 1024)),
        };

        ToolResultPayloadBounds.BoundResultMessages(messages, maxMessageBytes: cap, maxTotalBytes: int.MaxValue);

        messages[0].CalculateSize().Should().BeLessThanOrEqualTo(cap);
        messages[0].ToolCallId.Should().Be("call-1");
    }

    private static AgentRunChatMessage ToolResultMessage(string callId, string content) => new()
    {
        Role = "tool",
        ToolCallId = callId,
        Content = content,
    };
}
