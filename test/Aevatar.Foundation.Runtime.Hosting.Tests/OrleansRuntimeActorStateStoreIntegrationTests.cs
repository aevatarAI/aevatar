using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
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
                        .Register<ObserveAwareStatefulAgent>());
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
}
