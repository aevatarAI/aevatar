using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal sealed class RuntimeCallbackSchedulerGrainTestHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly List<RuntimeCallbackTimeoutRequest> _timeouts = [];

    private RuntimeCallbackSchedulerGrainTestHarness(IHost host)
    {
        _host = host;
    }

    public IActorRuntimeCallbackScheduler Scheduler { get; private set; } = null!;

    public List<RuntimeCallbackTimeoutRequest> Timeouts => _timeouts;

    public static async Task<RuntimeCallbackSchedulerGrainTestHarness> StartAsync()
    {
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: $"aevatar-channel-runtime-callback-test-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-channel-runtime-callback-test-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
            })
            .Build();

        await host.StartAsync();
        var harness = new RuntimeCallbackSchedulerGrainTestHarness(host);
        harness.Scheduler = new GrainBackedCallbackScheduler(
            host.Services.GetRequiredService<IGrainFactory>(),
            harness._timeouts);
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class GrainBackedCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly IGrainFactory _grainFactory;
        private readonly List<RuntimeCallbackTimeoutRequest> _timeouts;

        public GrainBackedCallbackScheduler(
            IGrainFactory grainFactory,
            List<RuntimeCallbackTimeoutRequest> timeouts)
        {
            _grainFactory = grainFactory;
            _timeouts = timeouts;
        }

        public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _timeouts.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            var generation = await _grainFactory
                .GetGrain<IRuntimeCallbackSchedulerGrain>(request.ActorId)
                .ScheduleTimeoutAsync(
                    request.CallbackId,
                    request.TriggerEnvelope.Clone(),
                    checked((int)request.DueTime.TotalMilliseconds),
                    request.DeliveryMode);

            return new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.Dedicated);
        }

        public async Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var generation = await _grainFactory
                .GetGrain<IRuntimeCallbackSchedulerGrain>(request.ActorId)
                .ScheduleTimerAsync(
                    request.CallbackId,
                    request.TriggerEnvelope.Clone(),
                    checked((int)request.DueTime.TotalMilliseconds),
                    checked((int)request.Period.TotalMilliseconds),
                    request.DeliveryMode);

            return new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.Dedicated);
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _grainFactory
                .GetGrain<IRuntimeCallbackSchedulerGrain>(lease.ActorId)
                .CancelAsync(lease.CallbackId, lease.Generation, lease.SlotEpoch);
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _grainFactory
                .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId)
                .PurgeAsync();
        }
    }
}
