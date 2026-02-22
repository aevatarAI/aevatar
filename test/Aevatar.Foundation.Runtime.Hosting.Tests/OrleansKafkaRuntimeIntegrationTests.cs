using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansKafkaRuntimeIntegrationTests
{
    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldDeliverEnvelopeToRuntimeActorGrain()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-it-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        RecordingKafkaIntegrationAgent.Reset();

        var host = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            siloPort,
            gatewayPort);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IKafkaEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "ping" }),
                Direction = EventDirection.Down,
            };

            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);
            var receivedEnvelope = await RecordingKafkaIntegrationAgent.WaitForEnvelopeAsync(TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("ping");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        string bootstrapServers,
        string topicName,
        string consumerGroup,
        string streamProviderName,
        string actorEventNamespace,
        int siloPort,
        int gatewayPort)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: $"aevatar-orleans-it-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-orleans-it-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaAdapter;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.QueueCount = 1;
                });
            })
            .ConfigureServices(services =>
            {
                services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
                {
                    options.BootstrapServers = bootstrapServers;
                    options.TopicName = topicName;
                    options.ConsumerGroup = consumerGroup;
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

    private static string RequireKafkaBootstrapServers() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS.");

    public sealed class RecordingKafkaIntegrationAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static TaskCompletionSource<EventEnvelope> _receivedEnvelope = CreateTcs();

        public string Id => "recording-kafka-integration-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (SyncLock)
            {
                _receivedEnvelope.TrySetResult(envelope.Clone());
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("recording-kafka-integration-agent");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public static void Reset()
        {
            lock (SyncLock)
            {
                _receivedEnvelope = CreateTcs();
            }
        }

        public static async Task<EventEnvelope> WaitForEnvelopeAsync(TimeSpan timeout)
        {
            Task<EventEnvelope> task;
            lock (SyncLock)
            {
                task = _receivedEnvelope.Task;
            }

            var completedTask = await Task.WhenAny(task, Task.Delay(timeout));
            if (completedTask != task)
                throw new TimeoutException($"Timed out after {timeout} waiting for Kafka envelope.");

            return await task;
        }

        private static TaskCompletionSource<EventEnvelope> CreateTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
