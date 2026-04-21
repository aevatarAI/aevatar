using Aevatar.GAgents.Channel.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkStreamingHandleTests
{
    [Fact]
    public async Task AppendAsync_OutOfOrderSequence_ReassemblesBySequenceNumber()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var handle = await adapter.BeginStreamingReplyAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("conformance-bot"),
                ConversationScope.DirectMessage,
                partition: null,
                "conformance-user"),
            new MessageContent(),
            CancellationToken.None);

        await handle.AppendAsync(new StreamChunk
        {
            Delta = "B",
            SequenceNumber = 2,
        });
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "A",
            SequenceNumber = 1,
        });

        var lastId = harness.HttpHandler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        harness.HttpHandler.ReadText(lastId).ShouldBe("AB");
    }
}
