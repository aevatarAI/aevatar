using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansRuntimeActorStateStoreIntegrationTests
{
    [Fact]
    public async Task RuntimeActorGrain_ShouldNotRestoreTransientStateWithoutEvents_WhenReinitialized()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.InitializeAgentByKindAsync("tests.state-store-aware-activation")).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");

            await grain.DeactivateAsync();

            (await grain.InitializeAgentByKindAsync("tests.state-store-aware-activation")).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_ShouldIgnoreObserveEnvelopes_WhenHandlingRuntimeInbox()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.InitializeAgentByKindAsync("tests.observe-aware-stateful")).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("handled-count:0");

            await grain.HandleEnvelopeAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "observe-only" }),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(string.Empty),
            }.ToByteArray());

            (await grain.GetDescriptionAsync()).Should().Be("handled-count:0");

            await grain.HandleEnvelopeAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "downstream" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            }.ToByteArray());

            (await grain.GetDescriptionAsync()).Should().Be("handled-count:1");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_PublicationRecoveryGap_ShouldFailActivationAndInboxDelivery()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.InitializeAgentByKindAsync("tests.publication-recovery-gap")).Should().BeTrue();
            await grain.DeactivateAsync();

            var eventStore = host.Services.GetRequiredService<IEventStore>();
            await eventStore.AppendAsync(
                actorId,
                [
                    BuildStateEvent(actorId, "committed-1", version: 1),
                    BuildStateEvent(actorId, "committed-2", version: 2),
                ],
                expectedVersion: 0);
            (await eventStore.DeleteEventsUpToAsync(actorId, 1)).Should().Be(1);

            var activation = () => grain.GetDescriptionAsync();
            await activation.Should().ThrowAsync<CommittedStatePublicationRecoveryException>();

            var delivery = () => grain.HandleEnvelopeAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "must-not-be-dropped" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                    string.Empty,
                    TopologyAudience.Children),
            }.ToByteArray());

            await delivery.Should().ThrowAsync<CommittedStatePublicationRecoveryException>();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_OrdinaryActivationFailure_ShouldKeepFalseInitializationContract()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.InitializeAgentByKindAsync("tests.ordinary-activation-failure"))
                .Should()
                .BeFalse();
            (await grain.IsInitializedAsync()).Should().BeFalse();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_EventStoreReadFailure_ShouldFailPersistedActivationAndInboxDelivery()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var eventStore = new FaultInjectingEventStore();
        var host = await StartSiloHostAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IEventStore>(eventStore)));

        try
        {
            var grain = host.Services
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            (await grain.InitializeAgentByKindAsync("tests.publication-recovery-gap")).Should().BeTrue();
            await grain.DeactivateAsync();
            eventStore.FailReads = true;

            await grain.Invoking(x => x.GetDescriptionAsync())
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected event-store recovery read failure.");
            await grain.Invoking(x => x.HandleEnvelopeAsync(BuildInboxEnvelope()))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected event-store recovery read failure.");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_CheckpointReadFailure_ShouldFailPersistedActivationAndInboxDelivery()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var publicationStore = new FaultInjectingPublicationStateStore();
        var host = await StartSiloHostAsync(services =>
            services.Replace(
                ServiceDescriptor.Singleton<ICommittedStatePublicationStateStore>(publicationStore)));

        try
        {
            var grain = host.Services
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            (await grain.InitializeAgentByKindAsync("tests.publication-recovery-gap")).Should().BeTrue();
            await grain.DeactivateAsync();
            publicationStore.FailLoads = true;

            await grain.Invoking(x => x.GetDescriptionAsync())
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected publication-checkpoint read failure.");
            await grain.Invoking(x => x.HandleEnvelopeAsync(BuildInboxEnvelope()))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected publication-checkpoint read failure.");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeActorGrain_RecoveryPublicationFailure_ShouldFailActivationAndInboxDelivery()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var eventStore = new FaultInjectingEventStore();
        var streamProvider = new FaultInjectingStreamProvider();
        var host = await StartSiloHostAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IEventStore>(eventStore));
            services.RemoveAll<Aevatar.Foundation.Abstractions.IStreamProvider>();
            services.RemoveAll<OrleansStreamProviderAdapter>();
            services.RemoveAll<IStreamLifecycleManager>();
            services.AddSingleton<Aevatar.Foundation.Abstractions.IStreamProvider>(streamProvider);
            services.AddSingleton<IStreamLifecycleManager, NoopStreamLifecycleManager>();
        });

        try
        {
            var grain = host.Services
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            (await grain.InitializeAgentByKindAsync("tests.publication-recovery-gap")).Should().BeTrue();
            await grain.DeactivateAsync();
            await eventStore.AppendAsync(
                actorId,
                [BuildStateEvent(actorId, "committed-1", version: 1)],
                expectedVersion: 0);
            streamProvider.FailProduces = true;

            var activationFailure = await grain.Invoking(x => x.GetDescriptionAsync())
                .Should()
                .ThrowAsync<CommittedStatePublicationException>();
            activationFailure.Which.Stage
                .Should()
                .Be(CommittedStatePublicationFailureStage.AdapterAcceptance);

            var deliveryFailure = await grain.Invoking(x => x.HandleEnvelopeAsync(BuildInboxEnvelope()))
                .Should()
                .ThrowAsync<CommittedStatePublicationException>();
            deliveryFailure.Which.Stage
                .Should()
                .Be(CommittedStatePublicationFailureStage.AdapterAcceptance);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static StateEvent BuildStateEvent(string actorId, string eventId, long version) =>
        new()
        {
            AgentId = actorId,
            EventId = eventId,
            Version = version,
            EventType = StringValue.Descriptor.FullName,
            EventData = Any.Pack(new StringValue { Value = eventId }),
        };

    private static byte[] BuildInboxEnvelope() => new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Payload = Any.Pack(new StringValue { Value = "must-not-be-dropped" }),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            string.Empty,
            TopologyAudience.Children),
    }.ToByteArray();

    private static async Task<IHost> StartSiloHostAsync(
        Action<IServiceCollection>? configureServices = null) =>
        await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: $"aevatar-orleans-state-store-it-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-orleans-state-store-it-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<StateStoreAwareActivationAgent>()
                        .Register<ObserveAwareStatefulAgent>()
                        .Register<PublicationRecoveryGapAgent>()
                        .Register<OrdinaryActivationFailureAgent>());
                    configureServices?.Invoke(services);
                });
            })
            .Build());

    [GAgent("tests.state-store-aware-activation")]
    public sealed class StateStoreAwareActivationAgent : GAgentBase<Int32Value>
    {
        protected override Task OnActivateAsync(CancellationToken ct)
        {
            State.Value += 1;
            return Task.CompletedTask;
        }

        public override Task<string> GetDescriptionAsync() =>
            Task.FromResult($"activation-count:{State.Value}");
    }

    [GAgent("tests.observe-aware-stateful")]
    public sealed class ObserveAwareStatefulAgent : GAgentBase<Int32Value>
    {
        [EventHandler]
        public Task HandleObserved(StringValue evt) =>
            PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

        protected override Int32Value TransitionState(Int32Value current, IMessage evt) =>
            StateTransitionMatcher
                .Match(current, evt)
                .On<StringValue>((state, _) => new Int32Value { Value = state.Value + 1 })
                .OrCurrent();

        public override Task<string> GetDescriptionAsync() =>
            Task.FromResult($"handled-count:{State.Value}");
    }

    [GAgent("tests.publication-recovery-gap")]
    public sealed class PublicationRecoveryGapAgent : GAgentBase<Int32Value>
    {
        [EventHandler]
        public Task Handle(StringValue evt) =>
            PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

        protected override Int32Value TransitionState(Int32Value current, IMessage evt) =>
            StateTransitionMatcher
                .Match(current, evt)
                .On<StringValue>((state, _) => new Int32Value { Value = state.Value + 1 })
                .OrCurrent();
    }

    [GAgent("tests.ordinary-activation-failure")]
    public sealed class OrdinaryActivationFailureAgent : GAgentBase<Int32Value>
    {
        protected override Task OnActivateAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Injected ordinary activation failure.");
    }

    private sealed class FaultInjectingEventStore : IEventStore, IEventStoreMaintenance
    {
        private readonly InMemoryEventStore _inner = new();
        private bool _failReads;

        public bool FailReads
        {
            get => Volatile.Read(ref _failReads);
            set => Volatile.Write(ref _failReads, value);
        }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default) =>
            _inner.AppendAsync(agentId, events, expectedVersion, ct);

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            FailReads
                ? Task.FromException<IReadOnlyList<StateEvent>>(
                    new InvalidOperationException("Injected event-store recovery read failure."))
                : _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            FailReads
                ? Task.FromException<long>(
                    new InvalidOperationException("Injected event-store recovery read failure."))
                : _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        public Task<bool> ResetStreamAsync(string agentId, CancellationToken ct = default) =>
            _inner.ResetStreamAsync(agentId, ct);
    }

    private sealed class FaultInjectingPublicationStateStore : ICommittedStatePublicationStateStore
    {
        private readonly InMemoryCommittedStatePublicationStateStore _inner = new();
        private bool _failLoads;

        public bool FailLoads
        {
            get => Volatile.Read(ref _failLoads);
            set => Volatile.Write(ref _failLoads, value);
        }

        public Task<CommittedStatePublicationState?> LoadAsync(
            string actorId,
            CancellationToken ct = default) =>
            FailLoads
                ? Task.FromException<CommittedStatePublicationState?>(
                    new InvalidOperationException("Injected publication-checkpoint read failure."))
                : _inner.LoadAsync(actorId, ct);

        public Task<CommittedStatePublicationState> InitializeAsync(
            string actorId,
            long baselinePublishedVersion,
            CancellationToken ct = default) =>
            _inner.InitializeAsync(actorId, baselinePublishedVersion, ct);

        public Task<CommittedStatePublicationState> AdvanceAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent publishedEvent,
            CancellationToken ct = default) =>
            _inner.AdvanceAsync(actorId, expectedPublishedVersion, publishedEvent, ct);

        public Task<CommittedStatePublicationState> RecordFailureAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent failedEvent,
            CommittedStatePublicationFailureStage stage,
            Exception error,
            CancellationToken ct = default) =>
            _inner.RecordFailureAsync(
                actorId,
                expectedPublishedVersion,
                failedEvent,
                stage,
                error,
                ct);
    }

    private sealed class FaultInjectingStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider
    {
        private bool _failProduces;

        public bool FailProduces
        {
            get => Volatile.Read(ref _failProduces);
            set => Volatile.Write(ref _failProduces, value);
        }

        public IStream GetStream(string actorId) => new FaultInjectingStream(actorId, this);

        private sealed class FaultInjectingStream(
            string streamId,
            FaultInjectingStreamProvider owner) : IStream
        {
            public string StreamId => streamId;

            public Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : IMessage
            {
                _ = message;
                ct.ThrowIfCancellationRequested();
                return owner.FailProduces
                    ? Task.FromException(
                        new InvalidOperationException("Injected committed-state publication failure."))
                    : Task.CompletedTask;
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(
                Func<T, Task> handler,
                CancellationToken ct = default)
                where T : IMessage, new()
            {
                _ = handler;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IAsyncDisposable>(NoopAsyncDisposable.Instance);
            }

            public Task UpsertRelayAsync(
                StreamForwardingBinding binding,
                CancellationToken ct = default)
            {
                _ = binding;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(
                string targetStreamId,
                CancellationToken ct = default)
            {
                _ = targetStreamId;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
            }
        }
    }

    private sealed class NoopStreamLifecycleManager : IStreamLifecycleManager
    {
        public void RemoveStream(string actorId) => _ = actorId;
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
