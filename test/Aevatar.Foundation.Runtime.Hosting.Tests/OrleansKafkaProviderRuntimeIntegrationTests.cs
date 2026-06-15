using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class OrleansKafkaProviderRuntimeIntegrationTests
{
    [KafkaGarnetIntegrationFact]
    public async Task KafkaProviderTransport_ShouldDeliverEnvelopeToRuntimeActorGrain()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-kafka-provider-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-kafka-provider-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-kafka-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.kafka.provider.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        RecordingKafkaProviderIntegrationAgent.Reset();

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
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingKafkaProviderIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<KafkaProviderProducer>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "kafka-provider-ping" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };

            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);
            var receivedEnvelope = await RecordingKafkaProviderIntegrationAgent.WaitForEnvelopeAsync(TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("kafka-provider-ping");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaProviderTransport_ShouldDeliverMessagesAcrossMultipleQueues_InMultiSiloSharedGroup()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topicName = $"aevatar-orleans-kafka-provider-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-kafka-provider-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-kafka-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.kafka.provider.it.{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-kafka-provider-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-kafka-provider-service-{Guid.NewGuid():N}";
        RecordingKafkaProviderIntegrationAgent.Reset();

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
            var mapper = new KafkaQueuePartitionMapper(streamProviderName, 4);
            var actorIds = FindActorIdsForAllPartitions(mapper, actorEventNamespace, 4);
            var grainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            await InitializeRuntimeActorsAsync(grainFactory, actorIds);

            var transport = host1.Services.GetRequiredService<KafkaProviderProducer>();
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

            var received = await RecordingKafkaProviderIntegrationAgent.WaitForEnvelopeValuesAsync(
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
    public async Task KafkaProviderTransport_ShouldDeliverMessagesWithoutWaitingForLocalHandoffAlignment()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topicName = $"aevatar-orleans-kafka-provider-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-kafka-provider-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-kafka-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.kafka.provider.it.{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-kafka-provider-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-kafka-provider-service-{Guid.NewGuid():N}";
        RecordingKafkaProviderIntegrationAgent.Reset();

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
            var mapper = new KafkaQueuePartitionMapper(streamProviderName, 4);
            var actorIds = FindActorIdsForAllPartitions(mapper, actorEventNamespace, 4);
            var grainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            await InitializeRuntimeActorsAsync(grainFactory, actorIds);

            var transport = host1.Services.GetRequiredService<KafkaProviderProducer>();
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

            var received = await RecordingKafkaProviderIntegrationAgent.WaitForEnvelopeValuesAsync(
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
    public async Task ForwardedCommittedObservation_ShouldReachTargetActorAcrossSilos()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var sourceActorId = $"committed-observation-source-{Guid.NewGuid():N}";
        var targetActorId = $"committed-observation-target-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-kafka-provider-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-kafka-provider-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-kafka-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.kafka.provider.it.{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-kafka-provider-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-kafka-provider-service-{Guid.NewGuid():N}";
        ForwardedCommittedObservationIntegrationAgent.Reset();
        CommittedObservationIntegrationAgent.Reset();

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
            var targetGrainFactory = host2.Services.GetRequiredService<IGrainFactory>();
            var targetGrain = targetGrainFactory.GetGrain<IRuntimeActorGrain>(targetActorId);
            (await targetGrain.InitializeAgentAsync(typeof(ForwardedCommittedObservationIntegrationAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            var streams = host2.Services.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
            await streams.GetStream(sourceActorId).UpsertRelayAsync(new StreamForwardingBinding
            {
                TargetStreamId = targetActorId,
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter = [],
                EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
                {
                    $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}",
                },
            });

            var sourceGrainFactory = host1.Services.GetRequiredService<IGrainFactory>();
            var sourceGrain = sourceGrainFactory.GetGrain<IRuntimeActorGrain>(sourceActorId);
            (await sourceGrain.InitializeAgentAsync(typeof(CommittedObservationIntegrationAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            await sourceGrain.HandleEnvelopeAsync(CreateDirectEnvelope(sourceActorId, "commit-me"));

            var observedEnvelope = await ForwardedCommittedObservationIntegrationAgent.WaitForObservationAsync(
                TimeSpan.FromSeconds(30));
            observedEnvelope.Payload!.Unpack<CommittedStateEventPublished>()
                .StateEvent!.EventData!.Unpack<StringValue>()
                .Value.Should().Be("commit-me");
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
    public async Task PersistedCommittedObservation_ShouldReachTargetActorSameSilo()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var sourceActorId = $"committed-observation-source-{Guid.NewGuid():N}";
        var targetActorId = $"committed-observation-target-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-kafka-provider-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-kafka-provider-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-kafka-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.kafka.provider.it.{Guid.NewGuid():N}";
        ForwardedCommittedObservationIntegrationAgent.Reset();
        CommittedObservationIntegrationAgent.Reset();

        var host = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            ReserveTcpPort(),
            ReserveTcpPort(),
            garnetConnectionString: garnetConnectionString,
            queueCount: 4,
            topicPartitionCount: 4);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();

            var targetGrain = grainFactory.GetGrain<IRuntimeActorGrain>(targetActorId);
            (await targetGrain.InitializeAgentAsync(typeof(ForwardedCommittedObservationIntegrationAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            var streams = host.Services.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
            await streams.GetStream(sourceActorId).UpsertRelayAsync(new StreamForwardingBinding
            {
                TargetStreamId = targetActorId,
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter = [],
                EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
                {
                    $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}",
                },
            });

            var sourceGrain = grainFactory.GetGrain<IRuntimeActorGrain>(sourceActorId);
            (await sourceGrain.InitializeAgentAsync(typeof(CommittedObservationIntegrationAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            await sourceGrain.HandleEnvelopeAsync(CreateDirectEnvelope(sourceActorId, "same-silo"));
            var signals = await CommittedObservationIntegrationAgent.WaitForSignalsAsync(2, TimeSpan.FromSeconds(10));
            signals.Should().Contain("received:same-silo");
            signals.Should().Contain("persisted:same-silo");

            var observedEnvelope = await ForwardedCommittedObservationIntegrationAgent.WaitForObservationAsync(
                TimeSpan.FromSeconds(30));
            observedEnvelope.Payload!.Unpack<CommittedStateEventPublished>()
                .StateEvent!.EventData!.Unpack<StringValue>()
                .Value.Should().Be("same-silo");
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
                    serviceId: serviceId ?? $"aevatar-orleans-kafka-provider-service-{Guid.NewGuid():N}",
                    clusterId: clusterId ?? $"aevatar-orleans-kafka-provider-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider;
                    options.PersistenceBackend = string.IsNullOrWhiteSpace(garnetConnectionString)
                        ? AevatarOrleansRuntimeOptions.PersistenceBackendInMemory
                        : AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = garnetConnectionString ?? string.Empty;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.QueueCount = queueCount;
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport();
            })
            .ConfigureServices(services =>
            {
                services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
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
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingKafkaProviderIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();
        }
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

    private static byte[] CreateDirectEnvelope(string actorId, string payload) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = payload }),
            Route = EnvelopeRouteSemantics.CreateDirect("kafka-provider-test", actorId),
        }.ToByteArray();

    private static IReadOnlyList<string> FindActorIdsForAllPartitions(
        KafkaQueuePartitionMapper mapper,
        string streamNamespace,
        int partitionCount)
    {
        var actorIds = new string[partitionCount];
        var remaining = partitionCount;
        for (var index = 0; index < 2048 && remaining > 0; index++)
        {
            var candidate = $"kafka-provider-actor-{index}";
            var partitionId = mapper.GetPartitionId(streamNamespace, candidate);
            if (actorIds[partitionId] != null)
                continue;

            actorIds[partitionId] = candidate;
            remaining--;
        }

        if (remaining > 0)
            throw new InvalidOperationException("Unable to find actor ids for all Kafka provider partitions.");

        return actorIds!;
    }

    public sealed class RecordingKafkaProviderIntegrationAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _receivedEnvelopes = CreateChannel();

        public string Id => "recording-kafka-provider-integration-agent";

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
            Task.FromResult("recording-kafka-provider-integration-agent");

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
                throw new TimeoutException($"Timed out after {timeout} waiting for Kafka provider envelope.");
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
                    $"Timed out after {timeout} waiting for {expectedCount} Kafka provider envelopes. Received {received.Count}: [{string.Join(", ", ordered)}].");
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

    public sealed class CommittedObservationIntegrationAgent : GAgentBase<Empty>
    {
        private static readonly Lock SyncLock = new();
        private static Channel<string> _signals = CreateSignalChannel();

        public static void Reset()
        {
            lock (SyncLock)
            {
                _signals = CreateSignalChannel();
            }
        }

        public static async Task<string> WaitForSignalAsync(TimeSpan timeout)
        {
            Channel<string> channel;
            lock (SyncLock)
            {
                channel = _signals;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await channel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for committed observation agent signal.");
            }
        }

        public static async Task<IReadOnlyList<string>> WaitForSignalsAsync(int count, TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);

            var results = new List<string>(count);
            for (var i = 0; i < count; i++)
                results.Add(await WaitForSignalAsync(timeout));

            return results;
        }

        [EventHandler]
        public async Task HandleObserved(StringValue evt)
        {
            WriteSignal($"received:{evt.Value}");
            try
            {
                await PersistDomainEventAsync(evt.Clone(), CancellationToken.None);
                WriteSignal($"persisted:{evt.Value}");
            }
            catch (Exception ex)
            {
                WriteSignal($"error:{ex.GetType().FullName}:{ex.Message}");
                throw;
            }
        }

        protected override Empty TransitionState(Empty current, IMessage evt) =>
            StateTransitionMatcher
                .Match(current, evt)
                .OrCurrent();

        private static void WriteSignal(string value)
        {
            lock (SyncLock)
            {
                _signals.Writer.TryWrite(value);
            }
        }

        private static Channel<string> CreateSignalChannel() =>
            Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public sealed class ForwardedCommittedObservationIntegrationAgent : GAgentBase<Empty>
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _observations = CreateChannel();

        [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
        public Task HandleForwardedEnvelope(EventEnvelope envelope)
        {
            if (envelope.Route.IsObserverPublication() &&
                StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) &&
                envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
            {
                lock (SyncLock)
                {
                    _observations.Writer.TryWrite(envelope.Clone());
                }
            }

            return Task.CompletedTask;
        }

        protected override Empty TransitionState(Empty current, IMessage evt) =>
            StateTransitionMatcher
                .Match(current, evt)
                .OrCurrent();

        public static void Reset()
        {
            lock (SyncLock)
            {
                _observations = CreateChannel();
            }
        }

        public static async Task<EventEnvelope> WaitForObservationAsync(TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _observations;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await channel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for forwarded committed observation.");
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
