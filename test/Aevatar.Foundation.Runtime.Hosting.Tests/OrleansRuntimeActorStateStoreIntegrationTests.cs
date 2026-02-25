using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
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
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var host = await StartSiloHostAsync(siloPort, gatewayPort);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var agentType = typeof(StateStoreAwareActivationAgent).AssemblyQualifiedName!;

            (await grain.InitializeAgentAsync(agentType)).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");

            await grain.DeactivateAsync();

            (await grain.InitializeAgentAsync(agentType)).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(int siloPort, int gatewayPort)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: $"aevatar-orleans-state-store-it-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-orleans-state-store-it-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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
}
