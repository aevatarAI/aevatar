using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorTransportDispatchTests
{
    [Fact]
    public void Constructor_WhenStreamProviderIsNull_ShouldThrowArgumentNullException()
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, SingleRuntimeActorGrainFactory>();

        var act = () => new OrleansActorDispatchPort(grainFactory, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("streams");
    }

    [Fact]
    public void Constructor_WhenGrainFactoryIsNull_ShouldThrowArgumentNullException()
    {
        var streams = new RecordingStreamProvider();

        var act = () => new OrleansActorDispatchPort(null!, streams);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("grainFactory");
    }

    [Fact]
    public async Task DispatchPortAsync_ShouldHandoffViaStreamProvider()
    {
        var grain = new RecordingRuntimeActorGrain();
        var streams = new RecordingStreamProvider();
        var grainFactory = DispatchProxy.Create<IGrainFactory, SingleRuntimeActorGrainFactory>();
        ((SingleRuntimeActorGrainFactory)(object)grainFactory).Grain = grain;
        var dispatchPort = new OrleansActorDispatchPort(
            grainFactory,
            streams);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await dispatchPort.DispatchAsync("actor-0", envelope, CancellationToken.None);

        streams.GetProduced("actor-0").Should().ContainSingle();
        streams.GetProduced("actor-0")[0].Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        grain.DispatchCount.Should().Be(0);
        grain.IsInitializedCallCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchPortAsync_ShouldValidateInputsBeforeResolvingGrain()
    {
        var grain = new RecordingRuntimeActorGrain();
        var streams = new RecordingStreamProvider();
        var grainFactory = DispatchProxy.Create<IGrainFactory, SingleRuntimeActorGrainFactory>();
        ((SingleRuntimeActorGrainFactory)(object)grainFactory).Grain = grain;
        var dispatchPort = new OrleansActorDispatchPort(grainFactory, streams);
        var envelope = new EventEnvelope();

        Func<Task> dispatchWithBlankActorId = async () =>
            await dispatchPort.DispatchAsync(" ", envelope, CancellationToken.None);
        Func<Task> dispatchWithNullEnvelope = async () =>
            await dispatchPort.DispatchAsync("actor-0", null!, CancellationToken.None);
        Func<Task> dispatchWithCanceledToken = async () =>
            await dispatchPort.DispatchAsync("actor-0", envelope, new CancellationToken(true));

        await dispatchWithBlankActorId.Should().ThrowAsync<ArgumentException>();
        await dispatchWithNullEnvelope.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("envelope");
        await dispatchWithCanceledToken.Should().ThrowAsync<OperationCanceledException>();
        grain.IsInitializedCallCount.Should().Be(0);
        grain.DispatchCount.Should().Be(0);
        streams.GetProduced("actor-0").Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchPortAsync_WhenActorIsNotInitialized_ShouldThrowBeforeHandoff()
    {
        var grain = new RecordingRuntimeActorGrain { Initialized = false };
        var streams = new RecordingStreamProvider();
        var grainFactory = DispatchProxy.Create<IGrainFactory, SingleRuntimeActorGrainFactory>();
        ((SingleRuntimeActorGrainFactory)(object)grainFactory).Grain = grain;
        var dispatchPort = new OrleansActorDispatchPort(
            grainFactory,
            streams);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        var act = () => dispatchPort.DispatchAsync("actor-0", envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Actor actor-0 is not initialized.");
        streams.GetProduced("actor-0").Should().BeEmpty();
        grain.DispatchCount.Should().Be(0);
    }

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
        public int IsInitializedCallCount { get; private set; }
        public bool Initialized { get; init; } = true;
        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task<bool> InitializeAgentByKindAsync(string kind) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync()
        {
            IsInitializedCallCount++;
            return Task.FromResult(Initialized);
        }

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            LastHandledEnvelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
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

        public Task<string> GetAgentKindAsync() => Task.FromResult(string.Empty);

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task PurgeAsync() => Task.CompletedTask;
    }

    private class SingleRuntimeActorGrainFactory : DispatchProxy
    {
        public IRuntimeActorGrain? Grain { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeActorGrain) &&
                args is { Length: > 0 } &&
                args[0] is string actorId &&
                Grain != null)
            {
                actorId.Should().Be("actor-0");
                return Grain;
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
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
