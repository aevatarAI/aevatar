using System.Reflection;
using System.Collections;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Callbacks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class InMemoryActorRuntimeCallbackSchedulerTests
{
    [Fact]
    public async Task CancelAsync_ShouldNotRemoveNewGeneration_WhenCalledWithStaleLease()
    {
        var scheduler = new InMemoryActorRuntimeCallbackScheduler(new RecordingStreamProvider());

        var firstLease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMinutes(1),
            TriggerEnvelope = CreateEnvelope(),
        });

        var secondLease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMinutes(1),
            TriggerEnvelope = CreateEnvelope(),
        });

        await scheduler.CancelAsync(firstLease);

        var current = GetScheduledCallback(scheduler, "actor-1", "cb-1");
        current.Should().NotBeNull();
        GetGeneration(current!).Should().Be(secondLease.Generation);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldReturnInMemoryBackendLease()
    {
        var scheduler = new InMemoryActorRuntimeCallbackScheduler(new RecordingStreamProvider());

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMinutes(1),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Backend.Should().Be(RuntimeCallbackBackend.InMemory);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_WhenUsingEnvelopeRedelivery_ShouldPublishOriginalPublisher()
    {
        var streams = new RecordingStreamProvider();
        var scheduler = new InMemoryActorRuntimeCallbackScheduler(streams);

        await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "parent-run",
            CallbackId = "retry-cb",
            DueTime = TimeSpan.FromMilliseconds(10),
            DeliveryMode = RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            TriggerEnvelope = new EventEnvelope
            {
                Id = "retry-envelope-3",
                Payload = Any.Pack(new StringValue { Value = "payload" }),
                Route = EnvelopeRouteSemantics.CreateDirect("child-run", "parent-run"),
            },
        });

        await streams.LastStreamProduced.Task.WaitAsync(TimeSpan.FromSeconds(2));

        streams.LastProduced.Should().NotBeNull();
        var produced = streams.LastProduced!;
        produced.Id.Should().Be("retry-envelope-3");
        produced.Route!.PublisherActorId.Should().Be("child-run");
        produced.Route.IsDirect().Should().BeTrue();
        produced.Route.GetTargetActorId().Should().Be("parent-run");
        produced.Runtime.Should().BeNull();
    }

    private static EventEnvelope CreateEnvelope() => new()
    {
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Route = EnvelopeRouteSemantics.CreateDirect("actor-1", "actor-1"),
    };

    private static object? GetScheduledCallback(
        InMemoryActorRuntimeCallbackScheduler scheduler,
        string actorId,
        string callbackId)
    {
        var field = typeof(InMemoryActorRuntimeCallbackScheduler)
            .GetField("_callbacks", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var callbacks = field!.GetValue(scheduler);
        callbacks.Should().NotBeNull();

        foreach (var entry in (IEnumerable)callbacks!)
        {
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry);
            if (key == null)
                continue;

            var currentActorId = key.GetType().GetProperty("ActorId")?.GetValue(key) as string;
            var currentCallbackId = key.GetType().GetProperty("CallbackId")?.GetValue(key) as string;
            if (!string.Equals(currentActorId, actorId, StringComparison.Ordinal) ||
                !string.Equals(currentCallbackId, callbackId, StringComparison.Ordinal))
            {
                continue;
            }

            return entry.GetType().GetProperty("Value")?.GetValue(entry);
        }

        return null;
    }

    private static long GetGeneration(object scheduledCallback)
    {
        var property = scheduledCallback.GetType().GetProperty("Generation");
        property.Should().NotBeNull();
        return (long)property!.GetValue(scheduledCallback)!;
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public EventEnvelope? LastProduced { get; private set; }

        public TaskCompletionSource<bool> LastStreamProduced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IStream GetStream(string actorId) => new RecordingStream(actorId, this);

        private sealed class RecordingStream(string actorId, RecordingStreamProvider owner) : IStream
        {
            public string StreamId { get; } = actorId;

            public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
            {
                ct.ThrowIfCancellationRequested();
                owner.LastProduced = message as EventEnvelope;
                owner.LastStreamProduced.TrySetResult(true);
                return Task.CompletedTask;
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new()
            {
                _ = handler;
                _ = ct;
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
            }

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                _ = binding;
                _ = ct;
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                _ = targetStreamId;
                _ = ct;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
            {
                _ = ct;
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
            }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
