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

    private static EventEnvelope CreateEnvelope() => new()
    {
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Direction = EventDirection.Self,
        TargetActorId = "actor-1",
        PublisherId = "actor-1",
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
        public IStream GetStream(string actorId) => new RecordingStream(actorId);
    }

    private sealed class RecordingStream(string actorId) : IStream
    {
        public string StreamId { get; } = actorId;

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            _ = message;
            _ = ct;
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

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
