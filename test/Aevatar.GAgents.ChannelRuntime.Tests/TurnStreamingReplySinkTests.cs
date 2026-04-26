using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
    public async Task OnDeltaAsync_WithinThrottle_DefersUntilTimerFires()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk 1", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await sink.OnDeltaAsync("chunk 1 more", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await sink.OnDeltaAsync("chunk 1 more text", CancellationToken.None);

        // Still inside the throttle window: only the first delta has dispatched. The subsequent
        // two are stashed; the deferred flush timer has not yet fired.
        envelopes.Should().ContainSingle();
        sink.ChunksEmitted.Should().Be(1);

        // Cross the throttle boundary so the deferred timer fires; only the latest stashed text
        // should publish (collapse-on-latest), not every individual delta.
        time.Advance(TimeSpan.FromMilliseconds(400));

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText
            .Should().Be("chunk 1 more text");
        sink.ChunksEmitted.Should().Be(2);
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
    public async Task FinalizeAsync_CancelsPendingFlushTimer()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await sink.OnDeltaAsync("chunk one two", CancellationToken.None);
        await sink.FinalizeAsync("final text", CancellationToken.None);

        // Finalize should publish the final text immediately and prevent the deferred timer from
        // firing afterwards (otherwise we'd see an extra "chunk one two" emission).
        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("final text");

        time.Advance(TimeSpan.FromMilliseconds(2000));
        envelopes.Should().HaveCount(2);
    }

    [Fact]
    public async Task FinalizeAsync_DispatchInFlight_WaitsForFinalChunkOnWire()
    {
        // Regression for the race where FinalizeAsync would return as soon as the final text
        // was stashed (while a prior dispatch was still in flight), letting the inbox runtime
        // send LlmReplyReadyEvent past the late final chunk and triggering the
        // ConversationGAgent processed-command guard to drop it.
        var firstDispatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var envelopes = new List<EventEnvelope>();
        var dispatchCount = 0;

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("target-actor");
        actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                envelopes.Add(call.Arg<EventEnvelope>());
                dispatchCount++;
                return dispatchCount == 1 ? firstDispatchGate.Task : Task.CompletedTask;
            });

        var sink = CreateSink(actor, throttleMs: 0, out _);

        // First dispatch enters the actor and suspends on firstDispatchGate.
        var deltaTask = sink.OnDeltaAsync("first", CancellationToken.None);

        // FinalizeAsync must observe _dispatchInProgress and wait for the dispatch loop's drain
        // signal — not return immediately after stashing the final text.
        var finalizeTask = sink.FinalizeAsync("first plus final", CancellationToken.None);

        deltaTask.IsCompleted.Should().BeFalse();
        finalizeTask.IsCompleted.Should().BeFalse();
        envelopes.Should().ContainSingle("only the gated first chunk has been dispatched");

        // Releasing the gate lets the loop dispatch the stashed final text; only then should
        // FinalizeAsync complete.
        firstDispatchGate.SetResult();

        await deltaTask;
        await finalizeTask;

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText
            .Should().Be("first plus final");
        sink.ChunksEmitted.Should().Be(2);
    }

    [Fact]
    public async Task PendingTimerEqualsLastEmitted_DoesNotEmitDuplicate()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("hello", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        // A duplicate "hello" inside the throttle window should clear any deferred copy and not
        // schedule a duplicate emission when the timer fires.
        await sink.OnDeltaAsync("hello", CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(1000));

        envelopes.Should().ContainSingle();
        sink.ChunksEmitted.Should().Be(1);
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

    [Fact]
    public async Task Dispose_PreventsLaterTimerFlush()
    {
        var (actor, envelopes) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("first", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.OnDeltaAsync("first plus more", CancellationToken.None);

        sink.Dispose();
        time.Advance(TimeSpan.FromMilliseconds(2000));

        // The deferred copy should be discarded by Dispose before the timer would have fired.
        envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task Dispose_AfterFinalize_IsIdempotent()
    {
        var (actor, _) = BuildRecordingActor();
        var sink = CreateSink(actor, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first", CancellationToken.None);
        await sink.FinalizeAsync("first plus", CancellationToken.None);

        sink.Dispose();
        sink.Dispose();
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
}
