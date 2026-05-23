using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf;
using ProtobufStringValue = Google.Protobuf.WellKnownTypes.StringValue;

namespace Aevatar.CQRS.Core.Tests;

public sealed class StreamActorOutcomeChannelTests
{
    [Fact]
    public async Task DispatchAndAwaitOutcomeAsync_ShouldSubscribeBeforeDispatch_AndReturnActorOutcome()
    {
        var target = new FakeCommandTarget("actor-1");
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);
        var dispatcher = new OrderedOutcomePublishingDispatcher(channel, provider);
        var pipeline = CreatePipeline(target, dispatcher, "receipt-1");
        var service =
            new DefaultCommandOutcomeDispatchService<SeededCommand, FakeCommandTarget, string, FakeError, ProtobufStringValue>(
                pipeline,
                channel);

        var result = await service.DispatchAndAwaitOutcomeAsync(new SeededCommand(
            "hello",
            "cmd-happy",
            "corr-1",
            null));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt-1");
        result.Outcome.Should().NotBeNull();
        result.Outcome!.Value.Should().Be("outcome:cmd-happy");
        dispatcher.SawSubscriberBeforePublish.Should().BeTrue();
        provider.ActiveSubscriberCount(StreamId("cmd-happy")).Should().Be(0);
    }

    [Fact]
    public async Task DispatchAndAwaitOutcomeAsync_WhenDispatchFailsAfterSubscribe_ShouldThrowAndDisposeSubscription()
    {
        var target = new FakeCommandTarget("actor-1");
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);
        var pipeline = CreatePipeline(target, new ThrowingTargetDispatcher(), "receipt-1");
        var service =
            new DefaultCommandOutcomeDispatchService<SeededCommand, FakeCommandTarget, string, FakeError, ProtobufStringValue>(
                pipeline,
                channel);

        var act = () => service.DispatchAndAwaitOutcomeAsync(new SeededCommand(
            "hello",
            "cmd-dispatch-fails",
            "corr-1",
            null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        provider.ActiveSubscriberCount(StreamId("cmd-dispatch-fails")).Should().Be(0);
    }

    [Fact]
    public async Task DispatchAndAwaitOutcomeAsync_WhenOutcomeWaitIsCanceled_ShouldIgnoreLateOutcomeAndDisposeSubscription()
    {
        var target = new FakeCommandTarget("actor-1");
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);
        var dispatcher = new RecordingTargetDispatcher();
        var pipeline = CreatePipeline(target, dispatcher, "receipt-1");
        var service =
            new DefaultCommandOutcomeDispatchService<SeededCommand, FakeCommandTarget, string, FakeError, ProtobufStringValue>(
                pipeline,
                channel);
        using var cts = new CancellationTokenSource();

        var dispatch = service.DispatchAndAwaitOutcomeAsync(new SeededCommand(
            "hello",
            "cmd-timeout",
            "corr-1",
            null), cts.Token);

        await provider.WaitForSubscriberAsync(StreamId("cmd-timeout"));
        dispatcher.Calls.Should().ContainSingle();
        await cts.CancelAsync();

        var act = async () => await dispatch;

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.ActiveSubscriberCount(StreamId("cmd-timeout")).Should().Be(0);

        await channel.PublishAsync("cmd-timeout", new ProtobufStringValue { Value = "late" });

        provider.ActiveSubscriberCount(StreamId("cmd-timeout")).Should().Be(0);
    }

    [Fact]
    public async Task SubscribeAsync_WithConcurrentSubscribersForSameActor_ShouldKeepIndependentOutcomeStreams()
    {
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);

        await using var first = await channel.SubscribeAsync("cmd-actor-1-a");
        await using var second = await channel.SubscribeAsync("cmd-actor-1-b");

        await channel.PublishAsync("cmd-actor-1-b", new ProtobufStringValue { Value = "second" });
        await channel.PublishAsync("cmd-actor-1-a", new ProtobufStringValue { Value = "first" });

        var firstOutcome = await first.Outcome;
        var secondOutcome = await second.Outcome;

        firstOutcome.Value.Should().Be("first");
        secondOutcome.Value.Should().Be("second");
    }

    [Fact]
    public async Task PublishAsync_AfterStreamRestart_ShouldUseRestartedStreamAndNotCompleteOldSubscriber()
    {
        var provider = new InMemoryStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);

        await using var oldSubscription = await channel.SubscribeAsync("cmd-restart");
        provider.RemoveStream(StreamId("cmd-restart"));

        await using var newSubscription = await channel.SubscribeAsync("cmd-restart");
        await channel.PublishAsync("cmd-restart", new ProtobufStringValue { Value = "after-restart" });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var newOutcome = await newSubscription.Outcome.WaitAsync(timeout.Token);

        newOutcome.Value.Should().Be("after-restart");
        oldSubscription.Outcome.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent_AndLateDuplicateOutcomesShouldBeIgnored()
    {
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);
        var subscription = await channel.SubscribeAsync("cmd-duplicate");

        await channel.PublishAsync("cmd-duplicate", new ProtobufStringValue { Value = "first" });
        var outcome = await subscription.Outcome;

        outcome.Value.Should().Be("first");

        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        await channel.PublishAsync("cmd-duplicate", new ProtobufStringValue { Value = "second" });

        var retainedOutcome = await subscription.Outcome;

        retainedOutcome.Value.Should().Be("first");
        provider.ActiveSubscriberCount(StreamId("cmd-duplicate")).Should().Be(0);
        provider.DisposeCallCount(StreamId("cmd-duplicate")).Should().Be(1);
    }

    [Fact]
    public async Task SubscribeAsync_ShouldNotCompleteFromWrongCommandId()
    {
        var provider = new TrackingStreamProvider();
        var channel = new StreamActorOutcomeChannel<ProtobufStringValue>(provider);

        await using var subscription = await channel.SubscribeAsync("cmd-right");
        await channel.PublishAsync("cmd-wrong", new ProtobufStringValue { Value = "wrong" });
        await using var wrongCommandSubscription = await channel.SubscribeAsync("cmd-wrong");
        await channel.PublishAsync("cmd-wrong", new ProtobufStringValue { Value = "wrong-observed" });

        var wrongOutcome = await wrongCommandSubscription.Outcome;

        wrongOutcome.Value.Should().Be("wrong-observed");
        subscription.Outcome.IsCompleted.Should().BeFalse();
    }

    private static DefaultCommandDispatchPipeline<SeededCommand, FakeCommandTarget, string, FakeError> CreatePipeline(
        FakeCommandTarget target,
        ICommandTargetDispatcher<FakeCommandTarget> dispatcher,
        string receipt)
    {
        return new DefaultCommandDispatchPipeline<SeededCommand, FakeCommandTarget, string, FakeError>(
            new SeededCommandResolver(target),
            new DefaultCommandContextPolicy(),
            new SeededCommandEnvelopeFactory(),
            dispatcher,
            new SeededCommandReceiptFactory(receipt));
    }

    private static string StreamId(string commandId) =>
        $"cqrs.actor-outcome:{ProtobufStringValue.Descriptor.FullName}:{commandId}";

    private sealed class OrderedOutcomePublishingDispatcher(
        IActorOutcomeChannel<ProtobufStringValue> channel,
        TrackingStreamProvider provider)
        : ICommandTargetDispatcher<FakeCommandTarget>
    {
        public bool SawSubscriberBeforePublish { get; private set; }

        public async Task DispatchAsync(
            FakeCommandTarget target,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = target;
            ct.ThrowIfCancellationRequested();
            var streamId = StreamId(envelope.Id);
            SawSubscriberBeforePublish = provider.ActiveSubscriberCount(streamId) == 1;
            await channel.PublishAsync(envelope.Id, new ProtobufStringValue { Value = $"outcome:{envelope.Id}" }, ct);
        }
    }

    private sealed class TrackingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, TrackingStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId)
        {
            lock (_streams)
            {
                if (!_streams.TryGetValue(actorId, out var stream))
                {
                    stream = new TrackingStream(actorId);
                    _streams.Add(actorId, stream);
                }

                return stream;
            }
        }

        public int ActiveSubscriberCount(string streamId) => TryGetStream(streamId)?.ActiveSubscriberCount ?? 0;

        public int DisposeCallCount(string streamId) => TryGetStream(streamId)?.DisposeCallCount ?? 0;

        public Task WaitForSubscriberAsync(string streamId) =>
            ((TrackingStream)GetStream(streamId)).WaitForSubscriberAsync();

        private TrackingStream? TryGetStream(string streamId)
        {
            lock (_streams)
            {
                return _streams.GetValueOrDefault(streamId);
            }
        }
    }

    private sealed class TrackingStream(string streamId) : IStream
    {
        private readonly object _gate = new();
        private readonly List<Func<IMessage, Task>> _subscribers = [];
        private readonly TaskCompletionSource _subscriberAdded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string StreamId { get; } = streamId;

        public int ActiveSubscriberCount
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers.Count;
                }
            }
        }

        public int DisposeCallCount { get; private set; }

        public async Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage
        {
            ArgumentNullException.ThrowIfNull(message);
            ct.ThrowIfCancellationRequested();

            Func<IMessage, Task>[] subscribers;
            lock (_gate)
            {
                subscribers = _subscribers.ToArray();
            }

            foreach (var subscriber in subscribers)
            {
                ct.ThrowIfCancellationRequested();
                await subscriber(message);
            }
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default)
            where T : IMessage, new()
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            Func<IMessage, Task> subscriber = message =>
                message is T typed ? handler(typed) : Task.CompletedTask;

            lock (_gate)
            {
                _subscribers.Add(subscriber);
                _subscriberAdded.TrySetResult();
            }

            return Task.FromResult<IAsyncDisposable>(new TrackingSubscription(this, subscriber));
        }

        public Task UpsertRelayAsync(
            Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding binding,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding>> ListRelaysAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task WaitForSubscriberAsync() => _subscriberAdded.Task;

        private void Unsubscribe(Func<IMessage, Task> subscriber)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
                DisposeCallCount++;
            }
        }

        private sealed class TrackingSubscription(
            TrackingStream stream,
            Func<IMessage, Task> subscriber)
            : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                    stream.Unsubscribe(subscriber);

                return ValueTask.CompletedTask;
            }
        }
    }
}
