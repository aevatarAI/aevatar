using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public sealed class InMemoryStreamCoverageTests
{
    [Fact]
    public async Task ProduceAsync_WithTypedMessage_ShouldWrapAndDispatchToTypedSubscriber()
    {
        var stream = new InMemoryStream("s-typed");
        var tcs = new TaskCompletionSource<PingEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await stream.SubscribeAsync<PingEvent>(evt =>
        {
            tcs.TrySetResult(evt);
            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new PingEvent { Message = "hello" });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        received.Message.Should().Be("hello");
    }

    [Fact]
    public async Task ProduceAsync_WithEnvelope_ShouldDispatchToEnvelopeAndTypedSubscribers()
    {
        var stream = new InMemoryStream("s-envelope");
        var envelopeTcs = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var typedTcs = new TaskCompletionSource<PingEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subEnvelope = await stream.SubscribeAsync<EventEnvelope>(evt =>
        {
            envelopeTcs.TrySetResult(evt);
            return Task.CompletedTask;
        });
        await using var subTyped = await stream.SubscribeAsync<PingEvent>(evt =>
        {
            typedTcs.TrySetResult(evt);
            return Task.CompletedTask;
        });

        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new PingEvent { Message = "payload" }),
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Self),
        };

        await stream.ProduceAsync(envelope);

        (await envelopeTcs.Task.WaitAsync(TimeSpan.FromSeconds(2))).Id.Should().Be("evt-1");
        (await typedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2))).Message.Should().Be("payload");
    }

    [Fact]
    public async Task Subscription_Dispose_ShouldStopFurtherNotifications()
    {
        var stream = new InMemoryStream("s-dispose");
        var calls = 0;
        var firstCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sub = await stream.SubscribeAsync<PingEvent>(_ =>
        {
            var current = Interlocked.Increment(ref calls);
            if (current == 1)
                firstCall.TrySetResult(true);
            else if (current == 2)
                secondCall.TrySetResult(true);

            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new PingEvent { Message = "one" });
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sub.DisposeAsync();
        await stream.ProduceAsync(new PingEvent { Message = "two" });

        using var noSecondCall = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondCall.Task.WaitAsync(noSecondCall.Token));

        calls.Should().Be(1);
    }

    [Fact]
    public async Task ThrowOnSubscriberError_False_ShouldContinueDispatch()
    {
        var stream = new InMemoryStream(
            "s-continue",
            new InMemoryStreamOptions { ThrowOnSubscriberError = false });
        var received = new List<string>();
        var bothReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();

        await using var throwing = await stream.SubscribeAsync<PingEvent>(_ =>
            throw new InvalidOperationException("boom"));
        await using var collecting = await stream.SubscribeAsync<PingEvent>(evt =>
        {
            lock (gate)
            {
                received.Add(evt.Message);
                if (received.Count >= 2)
                    bothReceived.TrySetResult(true);
            }

            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new PingEvent { Message = "m1" });
        await stream.ProduceAsync(new PingEvent { Message = "m2" });
        await bothReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        received.Should().ContainInOrder("m1", "m2");
    }

    [Fact]
    public async Task ThrowOnSubscriberError_True_ShouldStopStream()
    {
        var stream = new InMemoryStream(
            "s-stop",
            new InMemoryStreamOptions { ThrowOnSubscriberError = true });
        var firstDispatchObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var throwing = await stream.SubscribeAsync<PingEvent>(_ =>
        {
            firstDispatchObserved.TrySetResult(true);
            throw new InvalidOperationException("boom");
        });

        await stream.ProduceAsync(new PingEvent { Message = "first" });
        await firstDispatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Func<Task> act = async () => await stream.ProduceAsync(new PingEvent { Message = "second" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DispatchSubscribersConcurrently_True_ShouldNotBlockFastSubscriber()
    {
        var stream = new InMemoryStream(
            "s-concurrent",
            new InMemoryStreamOptions { DispatchSubscribersConcurrently = true });
        var fast = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var slow = await stream.SubscribeAsync<PingEvent>(async _ =>
        {
            await slowGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });
        await using var quick = await stream.SubscribeAsync<PingEvent>(_ =>
        {
            fast.TrySetResult(true);
            return Task.CompletedTask;
        });

        await stream.ProduceAsync(new PingEvent { Message = "x" });
        await fast.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        slowGate.TrySetResult(true);
    }

    [Fact]
    public async Task DispatchSubscribersConcurrently_True_ShouldStillRunPostDispatchCallback()
    {
        var forwarded = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InMemoryStream(
            "s-concurrent-forward",
            new InMemoryStreamOptions { DispatchSubscribersConcurrently = true },
            onDispatchedAsync: envelope =>
            {
                forwarded.TrySetResult(envelope);
                return Task.CompletedTask;
            });

        await using var slow = await stream.SubscribeAsync<PingEvent>(async _ =>
        {
            await slowGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await stream.ProduceAsync(new PingEvent { Message = "forward" });
        var envelope = await forwarded.Task.WaitAsync(TimeSpan.FromMilliseconds(300));

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.Unpack<PingEvent>().Message.Should().Be("forward");
        slowGate.TrySetResult(true);
    }

    [Fact]
    public async Task Shutdown_ShouldCompleteAndRejectFurtherWrites()
    {
        var stream = new InMemoryStream("s-shutdown");
        stream.Shutdown();

        Func<Task> act = async () => await stream.ProduceAsync(new PingEvent { Message = "after-shutdown" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task StreamProvider_ShouldNotifyCreatedRemoved_AndSupportUnsubscribe()
    {
        var provider = new InMemoryStreamProvider();
        var created = new List<string>();
        var removed = new List<string>();

        using var createdSub = provider.SubscribeCreated(id => created.Add(id));
        using var removedSub = provider.SubscribeRemoved(id => removed.Add(id));

        var stream1a = provider.GetStream("actor-1");
        var stream1b = provider.GetStream("actor-1");
        var stream2 = provider.GetStream("actor-2");

        stream1a.Should().BeSameAs(stream1b);
        stream2.Should().NotBeSameAs(stream1a);
        created.Should().ContainInOrder("actor-1", "actor-2");

        provider.RemoveStream("actor-1");
        provider.RemoveStream("actor-1");

        removed.Should().ContainSingle().Which.Should().Be("actor-1");

        Func<Task> writeRemoved = async () => await stream1a.ProduceAsync(new PingEvent { Message = "x" });
        await writeRemoved.Should().ThrowAsync<Exception>();

        createdSub.Dispose();
        removedSub.Dispose();
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
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var called = false;

        using var sub1 = provider.SubscribeCreated(_ => throw new InvalidOperationException("ignore"));
        using var sub2 = provider.SubscribeCreated(_ => called = true);

        Action act = () => provider.GetStream("actor-best-effort");
        act.Should().NotThrow();
        called.Should().BeTrue();
    }
}
