using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorTransportDispatchTests
{
    [Fact]
    public async Task HandleEventAsync_ShouldDispatchViaStreamProvider()
    {
        var grain = new RecordingRuntimeActorGrain();
        var streams = new RecordingStreamProvider();
        var actor = new OrleansActor("actor-1", grain, streams);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await actor.HandleEventAsync(envelope, CancellationToken.None);

        streams.GetProduced("actor-1").Should().ContainSingle();
        streams.GetProduced("actor-1")[0].Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        grain.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task AgentProxyHandleEventAsync_ShouldDispatchViaStreamProvider()
    {
        var grain = new RecordingRuntimeActorGrain();
        var streams = new RecordingStreamProvider();
        var actor = new OrleansActor("actor-2", grain, streams);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await actor.Agent.HandleEventAsync(envelope, CancellationToken.None);

        streams.GetProduced("actor-2").Should().ContainSingle();
        streams.GetProduced("actor-2")[0].Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        grain.DispatchCount.Should().Be(0);
    }

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        public int DispatchCount { get; private set; }

        public Task<bool> InitializeAgentAsync(string agentTypeName) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = EventEnvelope.Parser.ParseFrom(envelopeBytes);
            DispatchCount++;
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId) => Task.CompletedTask;

        public Task RemoveChildAsync(string childId) => Task.CompletedTask;

        public Task SetParentAsync(string parentId) => Task.CompletedTask;

        public Task ClearParentAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetChildrenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetParentAsync() => Task.FromResult<string?>(null);

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");

        public Task<string> GetAgentTypeNameAsync() => Task.FromResult(string.Empty);

        public Task<RuntimeActorStateSnapshot?> GetStateSnapshotAsync() => Task.FromResult<RuntimeActorStateSnapshot?>(null);

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task PurgeAsync() => Task.CompletedTask;
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(actorId, out var stream))
                {
                    stream = new RecordingStream(actorId);
                    _streams[actorId] = stream;
                }

                return stream;
            }
        }

        public IReadOnlyList<EventEnvelope> GetProduced(string actorId)
        {
            lock (_lock)
            {
                return _streams.TryGetValue(actorId, out var stream)
                    ? stream.Messages.ToList()
                    : [];
            }
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId => streamId;

        public List<EventEnvelope> Messages { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();

            var envelope = message as EventEnvelope ?? new EventEnvelope
            {
                Payload = Any.Pack(message),
            };

            Messages.Add(envelope.Clone());
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpSubscription.Instance);
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _ = binding;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            _ = targetStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }

    private sealed class NoOpSubscription : IAsyncDisposable
    {
        public static NoOpSubscription Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
