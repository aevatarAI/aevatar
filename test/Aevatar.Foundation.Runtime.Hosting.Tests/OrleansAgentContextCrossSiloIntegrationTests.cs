using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansAgentContextCrossSiloIntegrationTests
{
    [Fact]
    public async Task AgentContextId_ShouldPropagateAcrossSilos()
    {
        var clusterId = $"ctx-cross-silo-{Guid.NewGuid():N}";
        var serviceId = $"ctx-cross-silo-service-{Guid.NewGuid():N}";
        var silo1Port = ReserveTcpPort();
        var silo1Gateway = ReserveTcpPort();
        var silo2Port = ReserveTcpPort();
        var silo2Gateway = ReserveTcpPort();

        var host1 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            silo1Port,
            silo1Gateway,
            primarySiloEndpoint: null);

        var host2 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            silo2Port,
            silo2Gateway,
            primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, silo1Port));

        try
        {
            var grainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            var caller = grainFactory.GetGrain<IContextProbeCallerGrain>("caller-fixed");
            var contextId = $"ctx-{Guid.NewGuid():N}";

            // Try multiple callee ids to make sure we hit a grain on another silo.
            var maxAttempts = 40;
            for (var i = 0; i < maxAttempts; i++)
            {
                var calleeId = $"callee-{i}-{Guid.NewGuid():N}";
                var probe = await caller.ProbeAsync(calleeId, contextId);
                var parsed = ParseProbe(probe);

                parsed.ReceivedContextId.Should().Be(contextId);
            }
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
            await host1.StopAsync();
            host1.Dispose();
        }
    }

    private static (string CallerRuntime, string CalleeRuntime, string ReceivedContextId) ParseProbe(string value)
    {
        var parts = value.Split('|', StringSplitOptions.None);
        parts.Should().HaveCount(3);
        return (parts[0], parts[1], parts[2]);
    }

    private static async Task<IHost> StartSiloHostAsync(
        string clusterId,
        string serviceId,
        int siloPort,
        int gatewayPort,
        IPEndPoint? primarySiloEndpoint)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                if (primarySiloEndpoint == null)
                {
                    siloBuilder.UseLocalhostClustering(
                        siloPort: siloPort,
                        gatewayPort: gatewayPort,
                        serviceId: serviceId,
                        clusterId: clusterId);
                }
                else
                {
                    siloBuilder.UseLocalhostClustering(
                        siloPort: siloPort,
                        gatewayPort: gatewayPort,
                        primarySiloEndpoint: primarySiloEndpoint,
                        serviceId: serviceId,
                        clusterId: clusterId);
                }

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

    public interface IContextProbeCallerGrain : IGrainWithStringKey
    {
        Task<string> ProbeAsync(string calleeId, string contextId);
    }

    public interface IContextProbeCalleeGrain : IGrainWithStringKey
    {
        Task<string> ReadContextAsync(string key);
    }

    public sealed class ContextProbeCallerGrain(IAgentContextAccessor contextAccessor) : Grain, IContextProbeCallerGrain
    {
        public async Task<string> ProbeAsync(string calleeId, string contextId)
        {
            var key = "traceId";
            var previous = contextAccessor.Context;
            var current = new AsyncLocalAgentContext();
            current.Set(key, contextId);
            contextAccessor.Context = current;

            try
            {
                var callee = GrainFactory.GetGrain<IContextProbeCalleeGrain>(calleeId);
                var calleePayload = await callee.ReadContextAsync(key);
                return $"{RuntimeIdentity}|{calleePayload}";
            }
            finally
            {
                contextAccessor.Context = previous;
            }
        }
    }

    public sealed class ContextProbeCalleeGrain(IAgentContextAccessor contextAccessor) : Grain, IContextProbeCalleeGrain
    {
        public Task<string> ReadContextAsync(string key)
        {
            var value = contextAccessor.Context?.Get<string>(key) ?? string.Empty;
            return Task.FromResult($"{RuntimeIdentity}|{value}");
        }
    }
}
