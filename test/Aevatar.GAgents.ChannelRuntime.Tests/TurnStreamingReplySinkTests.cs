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
    public async Task OnDeltaAsync_DispatchesActorApprovedChunkEvent()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, out _);

        await sink.OnDeltaAsync("hello", CancellationToken.None);

        envelopes.Should().ContainSingle();
        var chunk = envelopes[0].Payload.Unpack<LlmReplyStreamChunkEvent>();
        chunk.CorrelationId.Should().Be("corr-1");
        chunk.AccumulatedText.Should().Be("hello");
        chunk.ReplyToken.Should().Be("runtime-reply-token");
        chunk.ReplyTokenExpiresAtUnixMs.Should().Be(1770000000000);
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task FinalizeAsync_DispatchesActorApprovedFinalChunk()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, out _);

        await sink.FinalizeAsync("final text", CancellationToken.None);

        envelopes.Should().ContainSingle();
        envelopes[0].Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText.Should().Be("final text");
    }

    [Fact]
    public async Task OnDeltaAsync_CardMode_DispatchesCardChunkEvent()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, out _, cardMode: true);

        await sink.OnDeltaAsync("card text", CancellationToken.None);

        envelopes.Should().ContainSingle();
        var chunk = envelopes[0].Payload.Unpack<LlmReplyCardStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("card text");
        chunk.ReplyToken.Should().Be("runtime-reply-token");
        chunk.ReplyTokenExpiresAtUnixMs.Should().Be(1770000000000);
        chunk.RunId.Should().Be("run-1");
        envelopes[0].Route.Direct.TargetActorId.Should().Be("target-actor");
    }

    [Fact]
    public async Task OnDeltaAsync_EmptyText_IsIgnored()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, out _);

        await sink.OnDeltaAsync("   ", CancellationToken.None);
        await sink.OnDeltaAsync(string.Empty, CancellationToken.None);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task OnDeltaAsync_ActorDispatchThrows_DropsChunkWithoutPropagating()
    {
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DispatchAdmission>(new InvalidOperationException("boom")));
        var sink = CreateSink(dispatchPort, out _);

        var act = async () => await sink.OnDeltaAsync("hello", CancellationToken.None);

        await act.Should().NotThrowAsync();
        sink.ChunksEmitted.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_PreventsLaterDispatch()
    {
        var (dispatchPort, envelopes) = BuildRecordingDispatchPort();
        var sink = CreateSink(dispatchPort, out _);

        sink.Dispose();
        await sink.OnDeltaAsync("first", CancellationToken.None);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public void Source_ShouldNotContainSinkOwnedTimerOrPendingDispatchState()
    {
        // Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
        //   Old pattern: sink owned timer callbacks, pending business text, and in-flight dispatch loops.
        //   New principle: actor/run state owns coalescing and calls the sink only with approved snapshots.
        var source = File.ReadAllText(GetProductionSourcePath());

        source.Should().NotContain("_flushTimer");
        source.Should().NotContain("CreateTimer");
        source.Should().NotContain("_pendingText");
        source.Should().NotContain("_dispatchInProgress");
        source.Should().NotContain(string.Concat("Task", ".Run"));
        source.Should().NotContain("_ = DispatchAsync");
        source.Should().NotContain("_ = DispatchLoopAsync");
    }

    private static TurnStreamingReplySink CreateSink(
        IActorDispatchPort dispatchPort,
        out FakeTimeProvider timeProvider,
        bool cardMode = false)
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
            replyToken: "runtime-reply-token",
            replyTokenExpiresAtUnixMs: 1770000000000,
            runId: "run-1",
            timeProvider,
            NullLogger<TurnStreamingReplySink>.Instance,
            cardMode);
    }

    private static (IActorDispatchPort dispatchPort, List<EventEnvelope> envelopes) BuildRecordingDispatchPort()
    {
        var envelopes = new List<EventEnvelope>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(DispatchAdmissionFactory.Create(call.ArgAt<string>(0), call.ArgAt<EventEnvelope>(1))));
        dispatchPort.When(x => x.DispatchAsync("target-actor", Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => envelopes.Add(call.Arg<EventEnvelope>()));
        return (dispatchPort, envelopes);
    }

    private static string GetProductionSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "agents",
                "Aevatar.GAgents.Channel.Runtime",
                "TurnStreamingReplySink.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate TurnStreamingReplySink.cs from test output directory.");
    }
}
