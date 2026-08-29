using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorTransportDispatchTests
{
    [Fact]
    public void Constructor_WhenDependencyIsNull_ShouldThrowArgumentNullException()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var streams = new RecordingStreamProvider();
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();

        var missingGrainFactory = () => new OrleansActorDispatchPort(null!, streams, grainContextAccessor);
        var missingStreams = () => new OrleansActorDispatchPort(grainFactory, null!, grainContextAccessor);
        var missingGrainContextAccessor = () => new OrleansActorDispatchPort(grainFactory, streams, null!);

        missingGrainFactory.Should().Throw<ArgumentNullException>()
            .WithParameterName("grainFactory");
        missingStreams.Should().Throw<ArgumentNullException>()
            .WithParameterName("streams");
        missingGrainContextAccessor.Should().Throw<ArgumentNullException>()
            .WithParameterName("grainContextAccessor");
    }

    [Fact]
    public async Task DispatchPortAsync_WhenTargetAdmissionIsNotRequired_ShouldHandoffWithoutResolvingTargetGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var streams = new RecordingStreamProvider();
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        var dispatchPort = new OrleansActorDispatchPort(grainFactory, streams, grainContextAccessor);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await dispatchPort.DispatchAsync("actor-default", envelope, CancellationToken.None);

        streams.GetProduced("actor-default").Should().ContainSingle();
        streams.GetProduced("actor-default")[0].Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        grainFactory.DidNotReceiveWithAnyArgs().GetGrain<IRuntimeActorGrain>(default!);
    }

    [Fact]
    public async Task DispatchPortAsync_WhenTargetAdmissionIsRequired_ShouldEnterTargetGrainAdmission()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var streams = new RecordingStreamProvider();
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns((IGrainContext?)null);
        var grain = new RecordingRuntimeActorGrain();
        grainFactory.GetGrain<IRuntimeActorGrain>("actor-0").Returns(grain);
        var dispatchPort = new OrleansActorDispatchPort(grainFactory, streams, grainContextAccessor);
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { RequireTargetActorAdmission = true },
            },
        };

        await dispatchPort.DispatchAsync("actor-0", envelope, CancellationToken.None);

        grain.AdmissionCount.Should().Be(1);
        grain.LastAdmittedEnvelope!.Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        streams.GetProduced("actor-0").Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchPortAsync_WhenTargetIsCurrentGrain_ShouldHandoffViaStreamForNextTurn()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var streams = new RecordingStreamProvider();
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create("runtime-actor", "actor-self"));
        grainContextAccessor.GrainContext.Returns(grainContext);
        var grain = new RecordingRuntimeActorGrain(grainContext);
        grainFactory.GetGrain<IRuntimeActorGrain>("actor-self").Returns(grain);
        var dispatchPort = new OrleansActorDispatchPort(grainFactory, streams, grainContextAccessor);
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "next-turn" }),
            Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { RequireTargetActorAdmission = true },
            },
        };

        await dispatchPort.DispatchAsync("actor-self", envelope, CancellationToken.None);

        grain.AdmissionCount.Should().Be(0);
        streams.GetProduced("actor-self").Should().ContainSingle();
        streams.GetProduced("actor-self")[0].Payload!.Unpack<StringValue>().Value.Should().Be("next-turn");
    }

    [Fact]
    public async Task DispatchPortAsync_ShouldValidateInputsBeforeHandoff()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var streams = new RecordingStreamProvider();
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        var dispatchPort = new OrleansActorDispatchPort(grainFactory, streams, grainContextAccessor);
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
        streams.GetProduced("actor-0").Should().BeEmpty();
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

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain, IGrainBase
    {
        public RecordingRuntimeActorGrain(IGrainContext? grainContext = null)
        {
            GrainContext = grainContext ?? Substitute.For<IGrainContext>();
        }

        public IGrainContext GrainContext { get; }

        public int DispatchCount { get; private set; }
        public int AdmissionCount { get; private set; }
        public EventEnvelope? LastAdmittedEnvelope { get; private set; }
        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task<bool> InitializeAgentByKindAsync(string kind) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task AdmitEnvelopeAsync(byte[] envelopeBytes)
        {
            LastAdmittedEnvelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
            AdmissionCount++;
            return Task.CompletedTask;
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

        public Task OnActivateAsync(CancellationToken token) => Task.CompletedTask;

        public Task OnDeactivateAsync(DeactivationReason reason, CancellationToken token) => Task.CompletedTask;
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
