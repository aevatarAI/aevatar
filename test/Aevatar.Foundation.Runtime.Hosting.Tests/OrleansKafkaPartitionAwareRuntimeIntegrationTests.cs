using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class OrleansKafkaPartitionAwareRuntimeIntegrationTests
{
    [KafkaGarnetIntegrationFact]
    public async Task KafkaPartitionAwareTransport_ShouldDeliverEnvelopeToRuntimeActorGrain()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-strict-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-strict-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-strict-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.strict.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        RecordingStrictKafkaIntegrationAgent.Reset();

        var host = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            siloPort,
            gatewayPort,
            garnetConnectionString: garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingStrictKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IKafkaPartitionAwareEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "strict-ping" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };

            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);
            var receivedEnvelope = await RecordingStrictKafkaIntegrationAgent.WaitForEnvelopeAsync(TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("strict-ping");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPartitionAwareTransport_ShouldDeliverMessagesAcrossMultipleQueues_InMultiSiloSharedGroup()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topicName = $"aevatar-orleans-strict-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-strict-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-strict-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.strict.it.{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-strict-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-strict-service-{Guid.NewGuid():N}";
        RecordingStrictKafkaIntegrationAgent.Reset();

        var host1 = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            ReserveTcpPort(),
            ReserveTcpPort(),
            clusterId,
            serviceId,
            garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);
        var host2 = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            ReserveTcpPort(),
            ReserveTcpPort(),
            clusterId,
            serviceId,
            garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);

        try
        {
            await WaitForAssignedPartitionsAsync([host1, host2], 4, TimeSpan.FromSeconds(20));

            var mapper = new StrictQueuePartitionMapper(streamProviderName, 4);
            var actorIds = FindActorIdsForAllPartitions(mapper, actorEventNamespace, 4);
            var grainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            await InitializeRuntimeActorsAsync(grainFactory, actorIds);

            await WaitForLocalHandoffAlignmentAsync([host1, host2], TimeSpan.FromSeconds(20));

            var transport = host1.Services.GetRequiredService<IKafkaPartitionAwareEnvelopeTransport>();
            foreach (var actorId in actorIds)
            {
                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Payload = Any.Pack(new StringValue { Value = actorId }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
                };
                await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);
            }

            var received = await RecordingStrictKafkaIntegrationAgent.WaitForEnvelopeValuesAsync(
                value => actorIds.Contains(value, StringComparer.Ordinal),
                actorIds.Count,
                TimeSpan.FromSeconds(30));
            received.Should().BeEquivalentTo(actorIds);
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
            await host1.StopAsync();
            host1.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPartitionAwareTransport_ShouldDeliverMessagesWithoutWaitingForLocalHandoffAlignment()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topicName = $"aevatar-orleans-strict-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-strict-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-strict-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.strict.it.{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-strict-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-strict-service-{Guid.NewGuid():N}";
        RecordingStrictKafkaIntegrationAgent.Reset();

        var host1 = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            ReserveTcpPort(),
            ReserveTcpPort(),
            clusterId,
            serviceId,
            garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);
        var host2 = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            ReserveTcpPort(),
            ReserveTcpPort(),
            clusterId,
            serviceId,
            garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);

        try
        {
            await WaitForAssignedPartitionsAsync([host1, host2], 4, TimeSpan.FromSeconds(20));

            var mapper = new StrictQueuePartitionMapper(streamProviderName, 4);
            var actorIds = FindActorIdsForAllPartitions(mapper, actorEventNamespace, 4);
            var grainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            await InitializeRuntimeActorsAsync(grainFactory, actorIds);

            var transport = host1.Services.GetRequiredService<IKafkaPartitionAwareEnvelopeTransport>();
            foreach (var actorId in actorIds)
            {
                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Payload = Any.Pack(new StringValue { Value = actorId }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
                };
                await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);
            }

            var received = await RecordingStrictKafkaIntegrationAgent.WaitForEnvelopeValuesAsync(
                value => actorIds.Contains(value, StringComparer.Ordinal),
                actorIds.Count,
                TimeSpan.FromSeconds(30));
            received.Should().BeEquivalentTo(actorIds);
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
            await host1.StopAsync();
            host1.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        string bootstrapServers,
        string topicName,
        string consumerGroup,
        string streamProviderName,
        string actorEventNamespace,
        int siloPort,
        int gatewayPort,
        string? clusterId = null,
        string? serviceId = null,
        string? garnetConnectionString = null,
        int queueCount = 4,
        int topicPartitionCount = 4)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: serviceId ?? $"aevatar-orleans-strict-service-{Guid.NewGuid():N}",
                    clusterId: clusterId ?? $"aevatar-orleans-strict-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware;
                    options.PersistenceBackend = string.IsNullOrWhiteSpace(garnetConnectionString)
                        ? AevatarOrleansRuntimeOptions.PersistenceBackendInMemory
                        : AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = garnetConnectionString ?? string.Empty;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.QueueCount = queueCount;
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();
            })
            .ConfigureServices(services =>
            {
                services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport(options =>
                {
                    options.BootstrapServers = bootstrapServers;
                    options.TopicName = topicName;
                    options.ConsumerGroup = consumerGroup;
                    options.TopicPartitionCount = topicPartitionCount;
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task InitializeRuntimeActorsAsync(
        IGrainFactory grainFactory,
        IEnumerable<string> actorIds)
    {
        foreach (var actorId in actorIds)
        {
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingStrictKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();
        }
    }

    private static async Task WaitForAssignedPartitionsAsync(
        IReadOnlyList<IHost> hosts,
        int expectedDistinctPartitionCount,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ownedPartitions = hosts
                .SelectMany(host => host.Services.GetRequiredService<IPartitionAssignmentManager>().GetOwnedPartitions())
                .Distinct()
                .Count();
            if (ownedPartitions >= expectedDistinctPartitionCount)
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for {expectedDistinctPartitionCount} assigned strict Kafka partitions.");
    }

    private static async Task WaitForLocalHandoffAlignmentAsync(
        IReadOnlyList<IHost> hosts,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var aligned = true;
            foreach (var host in hosts)
            {
                var ownedPartitions = host.Services
                    .GetRequiredService<IPartitionAssignmentManager>()
                    .GetOwnedPartitions()
                    .ToHashSet();
                var localDeliveryPort = host.Services.GetRequiredService<ILocalDeliveryAckPort>();
                var handlersField = localDeliveryPort.GetType().GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic);
                var localHandlerPartitions = new HashSet<int>();
                if (handlersField?.GetValue(localDeliveryPort) is System.Collections.IDictionary handlers)
                {
                    foreach (System.Collections.DictionaryEntry entry in handlers)
                    {
                        if (entry.Key is int partitionId)
                            localHandlerPartitions.Add(partitionId);
                    }
                }

                if (!ownedPartitions.IsSubsetOf(localHandlerPartitions))
                {
                    aligned = false;
                    break;
                }
            }

            if (aligned)
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for strict local handoff alignment.");
    }

    private static string RequireKafkaBootstrapServers() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS.");

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IReadOnlyList<string> FindActorIdsForAllPartitions(
        StrictQueuePartitionMapper mapper,
        string streamNamespace,
        int partitionCount)
    {
        var actorIds = new string[partitionCount];
        var remaining = partitionCount;
        for (var index = 0; index < 2048 && remaining > 0; index++)
        {
            var candidate = $"strict-actor-{index}";
            var partitionId = mapper.GetPartitionId(streamNamespace, candidate);
            if (actorIds[partitionId] != null)
                continue;

            actorIds[partitionId] = candidate;
            remaining--;
        }

        if (remaining > 0)
            throw new InvalidOperationException("Unable to find actor ids for all strict partitions.");

        return actorIds!;
    }

    public sealed class RecordingStrictKafkaIntegrationAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _receivedEnvelopes = CreateChannel();

        public string Id => "recording-strict-kafka-integration-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (SyncLock)
            {
                _receivedEnvelopes.Writer.TryWrite(envelope.Clone());
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("recording-strict-kafka-integration-agent");

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
                _receivedEnvelopes = CreateChannel();
            }
        }

        public static async Task<EventEnvelope> WaitForEnvelopeAsync(TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _receivedEnvelopes;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await channel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for strict Kafka envelope.");
            }
        }

        public static async Task<IReadOnlyList<string>> WaitForEnvelopeValuesAsync(
            Func<string, bool> predicate,
            int expectedCount,
            TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _receivedEnvelopes;
            }

            var received = new HashSet<string>(StringComparer.Ordinal);
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (received.Count < expectedCount)
                {
                    var envelope = await channel.Reader.ReadAsync(cts.Token);
                    var value = envelope.Payload?.Is(StringValue.Descriptor) == true
                        ? envelope.Payload.Unpack<StringValue>().Value
                        : string.Empty;
                    if (predicate(value))
                        received.Add(value);
                }

                return received.ToArray();
            }
            catch (OperationCanceledException)
            {
                var ordered = received.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for {expectedCount} strict Kafka envelopes. Received {received.Count}: [{string.Join(", ", ordered)}].");
            }
        }

        private static Channel<EventEnvelope> CreateChannel() =>
            Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }
}
