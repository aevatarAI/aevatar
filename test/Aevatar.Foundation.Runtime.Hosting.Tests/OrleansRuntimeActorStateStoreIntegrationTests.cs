using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
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

    private static StateEvent BuildStateEvent(string actorId, string eventId, long version) =>
        new()
        {
            AgentId = actorId,
            EventId = eventId,
            Version = version,
            EventType = StringValue.Descriptor.FullName,
            EventData = Any.Pack(new StringValue { Value = eventId }),
        };

    private static async Task<IHost> StartSiloHostAsync() =>
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
}
