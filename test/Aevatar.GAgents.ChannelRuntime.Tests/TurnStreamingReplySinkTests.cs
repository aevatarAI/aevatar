using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class TurnStreamingReplySinkTests
{
    [Fact]
    public async Task OnDeltaAsync_FirstDelta_DispatchesChunkEventToActor()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 0, out _);

        await sink.OnDeltaAsync("hello", CancellationToken.None);

        envelopes.Should().ContainSingle();
        var chunk = envelopes[0].Payload.Unpack<LlmReplyStreamChunkEvent>();
        chunk.CorrelationId.Should().Be("corr-1");
        chunk.AccumulatedText.Should().Be("hello");
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task OnDeltaAsync_WithinThrottle_DropsSubsequentDeltas()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk 1", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await sink.OnDeltaAsync("chunk 1 more", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await sink.OnDeltaAsync("chunk 1 more text", CancellationToken.None);

        envelopes.Should().ContainSingle();
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task OnDeltaAsync_AfterThrottleElapses_DispatchesAgain()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(800));
        await sink.OnDeltaAsync("chunk one two", CancellationToken.None);

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("chunk one two");
    }

    [Fact]
    public async Task FinalizeAsync_BypassesThrottle()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.FinalizeAsync("final text", CancellationToken.None);

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("final text");
    }

    [Fact]
    public async Task FinalizeAsync_NoNewText_DoesNotEmitRedundantChunk()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 0, out _);

        await sink.OnDeltaAsync("same text", CancellationToken.None);
        await sink.FinalizeAsync("same text", CancellationToken.None);

        envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task OnDeltaAsync_EmptyText_IsIgnored()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 0, out _);

        await sink.OnDeltaAsync("   ", CancellationToken.None);
        await sink.OnDeltaAsync(string.Empty, CancellationToken.None);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task OnDeltaAsync_SameAsPreviousText_IsIgnored()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 0, out _);

        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);

        envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task OnDeltaAsync_ActorDispatchThrows_DropsChunkWithoutPropagating()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("target-actor");
        actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var sink = CreateSink(actor, throttleMs: 0, out _);

        var act = async () => await sink.OnDeltaAsync("hello", CancellationToken.None);

        await act.Should().NotThrowAsync();
        sink.ChunksEmitted.Should().Be(0);
    }

    private static TurnStreamingReplySink CreateSink(
        IActor actor,
        int throttleMs,
        out FakeTimeProvider timeProvider)
    {
        timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero));
        return new TurnStreamingReplySink(
            actor,
            correlationId: "corr-1",
            registrationId: "reg-1",
            activityTemplate: new ChatActivity
            {
                Id = "msg-1",
                ChannelId = ChannelId.From("lark"),
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("reg-1"),
                    ConversationScope.Group,
                    "oc_group_1",
                    "group",
                    "oc_group_1"),
                Content = new MessageContent { Text = "hi" },
                OutboundDelivery = new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-1",
                },
            },
            throttle: TimeSpan.FromMilliseconds(throttleMs),
            timeProvider,
            NullLogger<TurnStreamingReplySink>.Instance);
    }

    private static (IActor actor, List<EventEnvelope> envelopes) BuildRecordingActor()
    {
        var envelopes = new List<EventEnvelope>();
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("target-actor");
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => envelopes.Add(call.Arg<EventEnvelope>()));
        return (actor, envelopes);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
