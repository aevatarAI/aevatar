using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;
using Orleans.Streams;
using System.Reflection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorStreamSubscriptionTests
{
    [Fact]
    public async Task SubscribeAsync_WhenOrleansRejectsStaleSiloRoute_ShouldRetry()
    {
        var provider = new SubscriptionStreamProvider
        {
            SubscriptionFailuresRemaining = 1,
        };
        var stream = CreateStream(provider);

        await using var lease = await stream.SubscribeAsync<StringValue>(_ => Task.CompletedTask);

        provider.SubscribeAttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task SubscribeAsync_WhenFailureIsNotTopologyRejection_ShouldNotRetry()
    {
        var provider = new SubscriptionStreamProvider
        {
            SubscriptionException = new InvalidOperationException("subscription failure"),
        };
        var stream = CreateStream(provider);

        var act = () => stream.SubscribeAsync<StringValue>(_ => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscription failure");
        provider.SubscribeAttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task SubscribeAsync_WhenOrleansKeepsRejectingStaleSiloRoute_ShouldStopAfterAttemptLimit()
    {
        var provider = new SubscriptionStreamProvider
        {
            SubscriptionFailuresRemaining = int.MaxValue,
        };
        var stream = CreateStream(provider);

        var act = () => stream.SubscribeAsync<StringValue>(_ => Task.CompletedTask);

        await act.Should().ThrowAsync<OrleansMessageRejectionException>()
            .WithMessage("stale silo route");
        provider.SubscribeAttemptCount.Should().Be(5);
    }

    [Fact]
    public async Task SubscribeAsync_WhenTargetSiloIsTemporarilyUnavailable_ShouldRetry()
    {
        var provider = new SubscriptionStreamProvider
        {
            SubscriptionFailuresRemaining = 1,
            TransientSubscriptionException = CreateSiloUnavailableException(),
        };
        var stream = CreateStream(provider);

        await using var lease = await stream.SubscribeAsync<StringValue>(_ => Task.CompletedTask);

        provider.SubscribeAttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task SubscribeAsync_WhenTopologyRetryIsCancelled_ShouldStopWaiting()
    {
        var provider = new SubscriptionStreamProvider
        {
            SubscriptionFailuresRemaining = int.MaxValue,
        };
        var stream = new OrleansActorStream(
            streamId: "actor-1",
            streamNamespace: "aevatar.events",
            streamProvider: provider,
            subscribeAttemptLimit: 30,
            subscribeRetryDelay: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        var act = () => stream.SubscribeAsync<StringValue>(
            _ => Task.CompletedTask,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.SubscribeAttemptCount.Should().Be(1);
    }

    private static OrleansActorStream CreateStream(SubscriptionStreamProvider provider) =>
        new(
            streamId: "actor-1",
            streamNamespace: "aevatar.events",
            streamProvider: provider,
            subscribeAttemptLimit: 5,
            subscribeRetryDelay: TimeSpan.Zero);

    private static SiloUnavailableException CreateSiloUnavailableException() =>
        (SiloUnavailableException)Activator.CreateInstance(
            typeof(SiloUnavailableException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["stale silo route"],
            culture: null)!;

    private sealed class SubscriptionStreamProvider : global::Orleans.Streams.IStreamProvider
    {
        private readonly SubscriptionAsyncStream _stream;

        public SubscriptionStreamProvider()
        {
            _stream = new SubscriptionAsyncStream(this);
        }

        public int SubscribeAttemptCount { get; private set; }

        public int SubscriptionFailuresRemaining { get; set; }

        public Exception? SubscriptionException { get; set; }

        public Exception? TransientSubscriptionException { get; set; }

        public string Name => "subscription-provider";

        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            _stream.StreamId = streamId;
            return (IAsyncStream<T>)(object)_stream;
        }

        private sealed class SubscriptionAsyncStream : IAsyncStream<EventEnvelope>
        {
            private readonly SubscriptionStreamProvider _owner;

            public SubscriptionAsyncStream(SubscriptionStreamProvider owner)
            {
                _owner = owner;
            }

            public bool IsRewindable => false;

            public string ProviderName => _owner.Name;

            public StreamId StreamId { get; set; }

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncObserver<EventEnvelope> observer)
            {
                _ = observer;
                _owner.SubscribeAttemptCount++;
                if (_owner.SubscriptionException is not null)
                    throw _owner.SubscriptionException;

                if (_owner.SubscriptionFailuresRemaining > 0)
                {
                    _owner.SubscriptionFailuresRemaining--;
                    throw _owner.TransientSubscriptionException ?? CreateMessageRejectionException();
                }

                return Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(
                    new SubscriptionHandle(StreamId, ProviderName));
            }

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncObserver<EventEnvelope> observer,
                StreamSequenceToken? token,
                string? filterData = null) => SubscribeAsync(observer);

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncBatchObserver<EventEnvelope> observer) => throw new NotSupportedException();

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncBatchObserver<EventEnvelope> observer,
                StreamSequenceToken? token) => throw new NotSupportedException();

            public Task<IList<StreamSubscriptionHandle<EventEnvelope>>> GetAllSubscriptionHandles() =>
                Task.FromResult<IList<StreamSubscriptionHandle<EventEnvelope>>>([]);

            public Task OnNextAsync(EventEnvelope item, StreamSequenceToken? token = null) =>
                Task.CompletedTask;

            public Task OnNextBatchAsync(
                IEnumerable<EventEnvelope> batch,
                StreamSequenceToken? token = null) => Task.CompletedTask;

            public Task OnCompletedAsync() => Task.CompletedTask;

            public Task OnErrorAsync(Exception ex) => Task.CompletedTask;

            public bool Equals(IAsyncStream<EventEnvelope>? other) => ReferenceEquals(this, other);

            public int CompareTo(IAsyncStream<EventEnvelope>? other) =>
                ReferenceEquals(this, other) ? 0 : 1;

            private static OrleansMessageRejectionException CreateMessageRejectionException() =>
                (OrleansMessageRejectionException)Activator.CreateInstance(
                    typeof(OrleansMessageRejectionException),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: ["stale silo route"],
                    culture: null)!;
        }

        private sealed class SubscriptionHandle : StreamSubscriptionHandle<EventEnvelope>
        {
            public SubscriptionHandle(StreamId streamId, string providerName)
            {
                StreamId = streamId;
                ProviderName = providerName;
            }

            public override Guid HandleId { get; } = Guid.NewGuid();

            public override StreamId StreamId { get; }

            public override string ProviderName { get; }

            public override Task UnsubscribeAsync() => Task.CompletedTask;

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null) => Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncBatchObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null) => Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

            public override bool Equals(StreamSubscriptionHandle<EventEnvelope>? other) =>
                other is SubscriptionHandle handle && handle.HandleId == HandleId;
        }
    }
}
