using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class InMemoryStreamingCoverageTests
{
    [Fact]
    public async Task ProduceAsync_WithTypedMessage_ShouldWrapAndDispatchToTypedSubscriber()
    {
        var stream = new InMemoryStream("stream-typed");
        var received = new TaskCompletionSource<StringValue>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await stream.SubscribeAsync<StringValue>(value =>
        {
            received.TrySetResult(value);
            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new StringValue { Value = "hello" });
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Value.Should().Be("hello");
    }

    [Fact]
    public async Task ProduceAsync_WithEnvelope_ShouldDispatchToEnvelopeAndTypedSubscribers()
    {
        var stream = new InMemoryStream("stream-envelope");
        var receivedEnvelope = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedTyped = new TaskCompletionSource<StringValue>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var envelopeSubscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            receivedEnvelope.TrySetResult(envelope);
            return Task.CompletedTask;
        });

        await using var typedSubscription = await stream.SubscribeAsync<StringValue>(value =>
        {
            receivedTyped.TrySetResult(value);
            return Task.CompletedTask;
        });

        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Self,
            },
        };

        await stream.ProduceAsync(envelope);

        (await receivedEnvelope.Task.WaitAsync(TimeSpan.FromSeconds(2))).Id.Should().Be("evt-1");
        (await receivedTyped.Task.WaitAsync(TimeSpan.FromSeconds(2))).Value.Should().Be("payload");
    }

    [Fact]
    public async Task Subscription_Dispose_ShouldStopFurtherNotifications()
    {
        var stream = new InMemoryStream("stream-dispose");
        var firstDelivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var subscription = await stream.SubscribeAsync<StringValue>(_ =>
        {
            var current = Interlocked.Increment(ref callCount);
            if (current == 1)
                firstDelivery.TrySetResult(true);
            else if (current == 2)
                secondDelivery.TrySetResult(true);
            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new StringValue { Value = "one" });
        await firstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await subscription.DisposeAsync();
        await stream.ProduceAsync(new StringValue { Value = "two" });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondDelivery.Task.WaitAsync(timeout.Token));
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ThrowOnSubscriberError_False_ShouldContinueDispatch()
    {
        var stream = new InMemoryStream(
            "stream-continue",
            new InMemoryStreamOptions { ThrowOnSubscriberError = false });
        var received = new List<string>();
        var bothReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Lock();

        await using var thrower = await stream.SubscribeAsync<StringValue>(_ =>
            throw new InvalidOperationException("boom"));

        await using var collector = await stream.SubscribeAsync<StringValue>(value =>
        {
            lock (gate)
            {
                received.Add(value.Value);
                if (received.Count >= 2)
                    bothReceived.TrySetResult(true);
            }

            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new StringValue { Value = "m1" });
        await stream.ProduceAsync(new StringValue { Value = "m2" });
        await bothReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        received.Should().ContainInOrder("m1", "m2");
    }

    [Fact]
    public async Task ThrowOnSubscriberError_True_ShouldStopStream()
    {
        var stream = new InMemoryStream(
            "stream-stop",
            new InMemoryStreamOptions { ThrowOnSubscriberError = true });
        var firstDispatchObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var thrower = await stream.SubscribeAsync<StringValue>(_ =>
        {
            firstDispatchObserved.TrySetResult(true);
            throw new InvalidOperationException("boom");
        });

        await stream.ProduceAsync(new StringValue { Value = "first" });
        await firstDispatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForDispatchLoopToTerminateAsync(stream, TimeSpan.FromSeconds(2));

        Func<Task> writeAfterFailure = async () => await stream.ProduceAsync(new StringValue { Value = "second" });
        await writeAfterFailure.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DispatchSubscribersConcurrently_True_ShouldNotBlockFastSubscriber()
    {
        var stream = new InMemoryStream(
            "stream-concurrent",
            new InMemoryStreamOptions { DispatchSubscribersConcurrently = true });
        var fast = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var slow = await stream.SubscribeAsync<StringValue>(async _ =>
        {
            await slowGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await using var quick = await stream.SubscribeAsync<StringValue>(_ =>
        {
            fast.TrySetResult(true);
            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new StringValue { Value = "x" });
        await fast.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        slowGate.TrySetResult(true);
    }

    [Fact]
    public async Task DispatchSubscribersConcurrently_True_ShouldStillInvokePostDispatchCallback()
    {
        var forwarded = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InMemoryStream(
            "stream-concurrent-forward",
            new InMemoryStreamOptions { DispatchSubscribersConcurrently = true },
            onDispatchedAsync: envelope =>
            {
                forwarded.TrySetResult(envelope);
                return Task.CompletedTask;
            });

        await using var slow = await stream.SubscribeAsync<StringValue>(async _ =>
        {
            await slowGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await stream.ProduceAsync(new StringValue { Value = "forward" });
        var envelope = await forwarded.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
        envelope.Payload!.Unpack<StringValue>().Value.Should().Be("forward");

        slowGate.TrySetResult(true);
    }

    [Fact]
    public async Task Shutdown_ShouldCompleteAndRejectFurtherWrites()
    {
        var stream = new InMemoryStream("stream-shutdown");
        stream.Shutdown();

        Func<Task> act = async () => await stream.ProduceAsync(new StringValue { Value = "after-shutdown" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task RelayApis_ShouldValidateAndNormalizeBindingByCurrentStream()
    {
        StreamForwardingBinding? saved = null;
        string? removedTarget = null;

        var stream = new InMemoryStream(
            "source-stream",
            upsertRelayAsync: (binding, _) =>
            {
                saved = binding;
                return Task.CompletedTask;
            },
            removeRelayAsync: (target, _) =>
            {
                removedTarget = target;
                return Task.CompletedTask;
            },
            listRelaysAsync: _ =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                [
                    new StreamForwardingBinding
                    {
                        SourceStreamId = "source-stream",
                        TargetStreamId = "target-stream",
                    },
                ]));

        await Assert.ThrowsAsync<ArgumentNullException>(() => stream.UpsertRelayAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => stream.RemoveRelayAsync(""));

        var binding = new StreamForwardingBinding
        {
            SourceStreamId = "another-source",
            TargetStreamId = "target-stream",
            ForwardingMode = StreamForwardingMode.TransitOnly,
            DirectionFilter = [EventDirection.Up],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "a" },
            Version = 12,
            LeaseId = "lease-1",
        };

        await stream.UpsertRelayAsync(binding);
        saved.Should().NotBeNull();
        saved!.SourceStreamId.Should().Be("source-stream");
        saved.TargetStreamId.Should().Be("target-stream");
        saved.ForwardingMode.Should().Be(StreamForwardingMode.TransitOnly);
        saved.DirectionFilter.Should().BeEquivalentTo([EventDirection.Up]);
        saved.EventTypeFilter.Should().BeEquivalentTo(["a"]);
        saved.Version.Should().Be(12);
        saved.LeaseId.Should().Be("lease-1");

        var listed = await stream.ListRelaysAsync();
        listed.Should().ContainSingle(x => x.TargetStreamId == "target-stream");

        await stream.RemoveRelayAsync("target-stream");
        removedTarget.Should().Be("target-stream");
    }

    [Fact]
    public async Task StreamProvider_ShouldNotifyCreatedRemoved_AndSupportUnsubscribe()
    {
        var provider = new InMemoryStreamProvider();
        var created = new List<string>();
        var removed = new List<string>();

        using var createdSubscription = provider.SubscribeCreated(id => created.Add(id));
        using var removedSubscription = provider.SubscribeRemoved(id => removed.Add(id));

        var stream1a = provider.GetStream("actor-1");
        var stream1b = provider.GetStream("actor-1");
        var stream2 = provider.GetStream("actor-2");

        stream1a.Should().BeSameAs(stream1b);
        stream2.Should().NotBeSameAs(stream1a);
        created.Should().ContainInOrder("actor-1", "actor-2");

        provider.RemoveStream("actor-1");
        provider.RemoveStream("actor-1");
        removed.Should().ContainSingle().Which.Should().Be("actor-1");

        Func<Task> writeRemoved = async () => await stream1a.ProduceAsync(new StringValue { Value = "x" });
        await writeRemoved.Should().ThrowAsync<Exception>();

        createdSubscription.Dispose();
        removedSubscription.Dispose();
        provider.GetStream("actor-3");
        provider.RemoveStream("actor-2");

        created.Should().NotContain("actor-3");
        removed.Should().NotContain("actor-2");
    }

    [Fact]
    public void StreamProvider_CallbackFailures_ShouldBeBestEffort()
    {
        var provider = new InMemoryStreamProvider(
            new InMemoryStreamOptions { Capacity = 4, FullMode = BoundedChannelFullMode.Wait },
            NullLoggerFactory.Instance);
        var invoked = false;

        using var badCallback = provider.SubscribeCreated(_ => throw new InvalidOperationException("ignore"));
        using var goodCallback = provider.SubscribeCreated(_ => invoked = true);

        Action act = () => provider.GetStream("actor-best-effort");
        act.Should().NotThrow();
        invoked.Should().BeTrue();
    }

    [Fact]
    public void StreamProvider_SubscribeCreatedAndRemoved_ShouldValidateCallback()
    {
        var provider = new InMemoryStreamProvider();
        provider.Invoking(x => x.SubscribeCreated(null!)).Should().Throw<ArgumentNullException>();
        provider.Invoking(x => x.SubscribeRemoved(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task StreamProvider_ShouldForwardToTargetStreamAndSetForwardingState()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var provider = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var source = provider.GetStream("source");
        var target = provider.GetStream("target");
        var received = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var targetSubscription = await target.SubscribeAsync<EventEnvelope>(envelope =>
        {
            received.TrySetResult(envelope);
            return Task.CompletedTask;
        });

        await source.UpsertRelayAsync(new StreamForwardingBinding
        {
            TargetStreamId = "target",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });

        await source.ProduceAsync(new StringValue { Value = "relay" });

        var forwarded = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        forwarded.Payload!.Unpack<StringValue>().Value.Should().Be("relay");
        StreamForwardingEnvelopeState.IsForwarded(forwarded).Should().BeTrue();
        StreamForwardingEnvelopeState.GetSourceStreamId(forwarded).Should().Be("source");
        StreamForwardingEnvelopeState.GetTargetStreamId(forwarded).Should().Be("target");
        StreamForwardingEnvelopeState.GetMode(forwarded).Should().Be(StreamForwardingHandleMode.HandleThenForward);
    }

    [Fact]
    public async Task StreamProvider_RemoveStream_ShouldCleanupIncomingAndOutgoingBindings()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var provider = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var source = provider.GetStream("source");
        var middle = provider.GetStream("middle");
        provider.GetStream("target");

        await source.UpsertRelayAsync(new StreamForwardingBinding { TargetStreamId = "middle" });
        await middle.UpsertRelayAsync(new StreamForwardingBinding { TargetStreamId = "target" });

        (await registry.ListBySourceAsync("source")).Should().ContainSingle(x => x.TargetStreamId == "middle");
        (await registry.ListBySourceAsync("middle")).Should().ContainSingle(x => x.TargetStreamId == "target");

        provider.RemoveStream("middle");

        (await registry.ListBySourceAsync("middle")).Should().BeEmpty();
        (await registry.ListBySourceAsync("source")).Should().BeEmpty();
    }

    [Fact]
    public void StreamProvider_WithRegistryMissingBindingSource_ShouldThrow()
    {
        Action act = () => _ = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new NonBindingSourceRegistry());

        act.Should().Throw<InvalidOperationException>().WithMessage("*IStreamForwardingBindingSource*");
    }

    private static async Task WaitForDispatchLoopToTerminateAsync(InMemoryStream stream, TimeSpan timeout)
    {
        var dispatchLoopField = typeof(InMemoryStream).GetField(
            "_dispatchLoop",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        dispatchLoopField.Should().NotBeNull("InMemoryStream should expose dispatch loop for deterministic test synchronization");

        var dispatchLoop = dispatchLoopField!.GetValue(stream) as Task;
        dispatchLoop.Should().NotBeNull();
        await dispatchLoop!.WaitAsync(timeout);
    }

    private sealed class NonBindingSourceRegistry : IStreamForwardingRegistry
    {
        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _ = binding;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            _ = targetStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }
}
