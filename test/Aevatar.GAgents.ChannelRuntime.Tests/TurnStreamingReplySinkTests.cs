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
    public async Task OnDeltaAsync_BeyondInterimCap_StashesButDoesNotDispatchUntilFinal()
    {
        // Lark caps message edits per om_id (~20 in mainnet, code 230072). Capping interim
        // dispatches in the sink keeps headroom so FinalizeAsync's edit always lands —
        // long replies freeze on the last interim until the final, which is preferable to
        // truncation. This test pins the contract: interim chunks past the cap stash but
        // do not dispatch; the final still goes through with the freshest accumulated text.
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _, maxInterimChunks: 2);

        await sink.OnDeltaAsync("chunk 1", CancellationToken.None);
        await sink.OnDeltaAsync("chunk 1 + 2", CancellationToken.None);
        await sink.OnDeltaAsync("chunk 1 + 2 + 3 (capped, stashed)", CancellationToken.None);
        await sink.OnDeltaAsync("chunk 1 + 2 + 4 (still capped)", CancellationToken.None);

        envelopes.Should().HaveCount(2, "interim chunks past the cap must stash, not dispatch");
        envelopes[0].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("chunk 1");
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("chunk 1 + 2");
        sink.ChunksEmitted.Should().Be(2);

        await sink.FinalizeAsync("complete final text after cap", CancellationToken.None);

        envelopes.Should().HaveCount(3, "FinalizeAsync must bypass the cap so the user sees the complete text");
        envelopes[2].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText
            .Should().Be("complete final text after cap");
    }

    [Fact]
    public async Task DispatchLoop_StashesDuringDispatch_DefersToTimerInsteadOfDrainingImmediately()
    {
        // Regression: previously the dispatch loop drained _pendingText at dispatch-rate
        // without re-checking _throttle, so streaming token bursts produced one Lark edit
        // per token and exhausted the per-message edit cap (~20 in mainnet, code 230072).
        // Pin the gate: when more deltas are stashed during a dispatch and the throttle
        // window has not elapsed by the time the dispatch completes, the loop must hand
        // off to the deferred flush timer instead of dispatching immediately.
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        var envelopes = new List<EventEnvelope>();
        var slowDispatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                envelopes.Add(call.Arg<EventEnvelope>());
                return Interlocked.Increment(ref dispatchCount) == 1 ? slowDispatch.Task : Task.CompletedTask;
            });

        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

        // First delta starts dispatch but is awaiting slowDispatch.
        var firstFlush = sink.OnDeltaAsync("chunk 1", CancellationToken.None);

        // Burst additional deltas while dispatch1 is in flight — they stash.
        await sink.OnDeltaAsync("chunk 1 + 2", CancellationToken.None);
        await sink.OnDeltaAsync("chunk 1 + 2 + 3 (latest)", CancellationToken.None);

        // Release dispatch1. The loop reaches its post-dispatch check, sees pending text,
        // observes the throttle window has not elapsed, and exits to arm the timer rather
        // than dispatching immediately. Without this gate, all three chunks dispatch in
        // rapid succession (the original bug).
        slowDispatch.SetResult(true);
        await firstFlush;

        envelopes.Should().ContainSingle("loop must defer when throttle window has not elapsed");

        // Advance across the throttle. The timer fires synchronously inside Advance and
        // re-enters DispatchLoopAsync to drain the freshest stashed text.
        time.Advance(TimeSpan.FromMilliseconds(800));

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText
            .Should().Be("chunk 1 + 2 + 3 (latest)");
    }

    [Fact]
    public async Task OnDeltaAsync_FirstDelta_DispatchesChunkEventToActor()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

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
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

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
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(800));
        await sink.OnDeltaAsync("chunk one two", CancellationToken.None);

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("chunk one two");
    }

    [Fact]
    public async Task FinalizeAsync_BypassesThrottle()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.FinalizeAsync("final text", CancellationToken.None);

        envelopes.Should().HaveCount(2);
        envelopes[1].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("final text");
    }

    [Fact]
    public async Task FinalizeAsync_NoNewText_DoesNotEmitRedundantChunk()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

        await sink.OnDeltaAsync("same text", CancellationToken.None);
        await sink.FinalizeAsync("same text", CancellationToken.None);

        envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task FinalizeAsync_CancelsPendingFlushTimer()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

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
        // was stashed (while a prior dispatch was still in flight), letting the run actor
        // send LlmReplyReadyEvent past the late final chunk and triggering the
        // ConversationGAgent processed-command guard to drop it.
        var firstDispatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var envelopes = new List<EventEnvelope>();
        var dispatchCount = 0;

        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                envelopes.Add(call.Arg<EventEnvelope>());
                dispatchCount++;
                return dispatchCount == 1 ? firstDispatchGate.Task : Task.CompletedTask;
            });

        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

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
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

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
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

        await sink.OnDeltaAsync("   ", CancellationToken.None);
        await sink.OnDeltaAsync(string.Empty, CancellationToken.None);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task OnDeltaAsync_SameAsPreviousText_IsIgnored()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);

        envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task OnDeltaAsync_ActorDispatchThrows_DropsChunkWithoutPropagating()
    {
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

        var act = async () => await sink.OnDeltaAsync("hello", CancellationToken.None);

        await act.Should().NotThrowAsync();
        sink.ChunksEmitted.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_PreventsLaterTimerFlush()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 750, out var time);

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
        var (dispatchPort, _) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first", CancellationToken.None);
        await sink.FinalizeAsync("first plus", CancellationToken.None);

        sink.Dispose();
        sink.Dispose();
    }

    private static TurnStreamingReplySink CreateSink(
        IActorDispatchPort dispatchPort,
        int throttleMs,
        out FakeTimeProvider timeProvider,
        int maxInterimChunks = int.MaxValue)
    {
        timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero));
        return new TurnStreamingReplySink(
            dispatchPort,
            "target-actor",
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
            NullLogger<TurnStreamingReplySink>.Instance,
            maxInterimChunks: maxInterimChunks);
    }

    private static (IActorDispatchPort dispatchPort, List<EventEnvelope> envelopes) BuildRecordingDispatchPort()
    {
        var envelopes = new List<EventEnvelope>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        dispatchPort.When(x => x.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => envelopes.Add(call.Arg<EventEnvelope>()));
        return (dispatchPort, envelopes);
    }
}
