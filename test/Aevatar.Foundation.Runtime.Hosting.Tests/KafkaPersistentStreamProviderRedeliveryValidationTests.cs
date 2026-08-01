using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Confluent.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Validation harness for the Orleans Kafka persistent stream provider
/// "throw-vs-return" redelivery contract (Channel RFC §9.5.6).
///
/// Asserts that the Kafka-backed Orleans persistent stream provider:
///   1. Does NOT redeliver an envelope when the subscriber's OnNextAsync returns normally.
///   2. DOES redeliver an envelope when the subscriber's OnNextAsync throws and the
///      throw is propagated (envelope.Runtime.Dispatch.PropagateFailure = true), including
///      through Aevatar's provisional duplicate filter.
///
/// Runs when AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS and
/// AEVATAR_TEST_GARNET_CONNECTION_STRING are set. The repository currently ships
/// InMemory and KafkaProvider stream backends only; if a new persistent backend is
/// added later, extend this harness explicitly instead of assuming Kafka semantics
/// carry over unchanged.
/// </summary>
[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class KafkaPersistentStreamProviderRedeliveryValidationTests
{
    private static readonly TimeSpan RedeliveryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NoRedeliveryQuietPeriod = TimeSpan.FromSeconds(10);

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenOnNextAsyncReturns_DoesNotRedeliver()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        OnNextAttemptRecorder.Reset();

        IHost? host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentByKindAsync("tests.always-succeed-on-next"))
                .Should().BeTrue();

            var envelopeId = Guid.NewGuid().ToString("N");
            await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: false);

            await OnNextAttemptRecorder.WaitForAttemptsAsync(envelopeId, expectedAttempts: 1, RedeliveryTimeout);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);
            await Task.Delay(NoRedeliveryQuietPeriod);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(
                1,
                "a persistent stream provider must NOT redeliver an envelope whose subscriber returned normally");
            await host.StopAsync();
            host.Dispose();
            host = await StartSiloHostAsync(
                bootstrapServers,
                garnetConnectionString,
                topology.WithFreshPorts());
            await Task.Delay(NoRedeliveryQuietPeriod);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(
                1,
                "a committed successful delivery must not be replayed after receiver restart");
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenOnNextAsyncThrows_RedeliversMessage()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        OnNextAttemptRecorder.Reset();
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "0",
        });

        var host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentByKindAsync("tests.throw-once-then-succeed"))
                .Should().BeTrue();

            var envelopeId = Guid.NewGuid().ToString("N");
            await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: true);

            await OnNextAttemptRecorder.WaitForAttemptsAsync(envelopeId, expectedAttempts: 2, RedeliveryTimeout);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().BeGreaterThanOrEqualTo(
                2,
                "a persistent stream provider must redeliver an envelope whose subscriber's OnNextAsync throws (checkpoint not advanced)");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenRetryExhaustedReturns_CommitsAndDoesNotRedeliverAfterRestart()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        OnNextAttemptRecorder.Reset();
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "0",
        });

        IHost? host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentByKindAsync("tests.always-fail-on-next"))
                .Should().BeTrue();

            var envelopeId = Guid.NewGuid().ToString("N");
            await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: false);
            await OnNextAttemptRecorder.WaitForAttemptsAsync(envelopeId, expectedAttempts: 1, RedeliveryTimeout);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(1);

            await host.StopAsync();
            host.Dispose();
            host = await StartSiloHostAsync(
                bootstrapServers,
                garnetConnectionString,
                topology.WithFreshPorts());
            await Task.Delay(NoRedeliveryQuietPeriod);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(
                1,
                "a committed default terminal failure must not poison the partition after restart");
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenActorUnavailableByDefault_CommitsOffset()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        var host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.IsInitializedAsync()).Should().BeFalse();

            await PublishEnvelopeAsync(
                host,
                topology,
                Guid.NewGuid().ToString("N"),
                propagateFailure: false);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenPayloadInvalid_CommitsOffsetAndRestartKeepsItCommitted()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        IHost? host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var producer = host.Services.GetRequiredService<KafkaProviderProducer>();
            await producer.PublishAsync(
                topology.ActorEventNamespace,
                topology.ActorId,
                [0xff, 0xff],
                CancellationToken.None);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);

            await host.StopAsync();
            host.Dispose();
            host = await StartSiloHostAsync(
                bootstrapServers,
                garnetConnectionString,
                topology.WithFreshPorts());
            await Task.Delay(NoRedeliveryQuietPeriod);

            ReadCommittedOffset(bootstrapServers, topology).Should().Be(new Offset(1));
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaReceiver_WhenBatchIsNotDelivered_ShouldRedeliverSameOffsetAfterRestart()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        _ = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        var options = new KafkaProviderTransportOptions
        {
            BootstrapServers = bootstrapServers,
            TopicName = topology.TopicName,
            ConsumerGroup = topology.ConsumerGroup,
            TopicPartitionCount = 4,
        };
        var mapper = new KafkaQueuePartitionMapper(topology.StreamProviderName, 4);
        await using var producer = new KafkaProviderProducer(options, mapper);
        KafkaProviderQueueAdapterReceiver? receiver = null;

        try
        {
            var queueId = mapper.GetQueueForStream(
                StreamId.Create(topology.ActorEventNamespace, topology.ActorId));
            receiver = new KafkaProviderQueueAdapterReceiver(
                queueId,
                producer,
                options,
                mapper,
                topology.ActorEventNamespace,
                NullLoggerFactory.Instance);
            await receiver.Initialize(TimeSpan.FromSeconds(10));

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "commit-window" }),
                Route = EnvelopeRouteSemantics.CreateDirect("commit-window-test", topology.ActorId),
            };
            await producer.PublishAsync(
                topology.ActorEventNamespace,
                topology.ActorId,
                envelope.ToByteArray(),
                CancellationToken.None);

            var firstDelivery = await WaitForReceiverBatchAsync(receiver, RedeliveryTimeout);
            await receiver.Shutdown(TimeSpan.FromSeconds(10));
            receiver = null;

            ReadCommittedOffset(bootstrapServers, topology).Should().NotBe(
                new Offset(firstDelivery.KafkaOffset + 1),
                "returning a batch from the receiver must not commit before MessagesDeliveredAsync");

            receiver = new KafkaProviderQueueAdapterReceiver(
                queueId,
                producer,
                options,
                mapper,
                topology.ActorEventNamespace,
                NullLoggerFactory.Instance);
            await receiver.Initialize(TimeSpan.FromSeconds(10));
            var redelivery = await WaitForReceiverBatchAsync(receiver, RedeliveryTimeout);

            redelivery.KafkaOffset.Should().Be(firstDelivery.KafkaOffset);
            await receiver.MessagesDeliveredAsync(new List<IBatchContainer> { redelivery });
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(redelivery.KafkaOffset + 1),
                RedeliveryTimeout);
        }
        finally
        {
            if (receiver != null)
                await receiver.Shutdown(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task PublishEnvelopeAsync(
        IHost host,
        TestTopology topology,
        string envelopeId,
        bool propagateFailure)
    {
        var envelope = new EventEnvelope
        {
            Id = envelopeId,
            Payload = Any.Pack(new StringValue { Value = envelopeId }),
            Route = EnvelopeRouteSemantics.CreateDirect("persistent-provider-validation", topology.ActorId),
        };
        if (propagateFailure)
        {
            envelope.Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { PropagateFailure = true },
            };
        }

        var producer = host.Services.GetRequiredService<KafkaProviderProducer>();
        await producer.PublishAsync(
            topology.ActorEventNamespace,
            topology.ActorId,
            envelope.ToByteArray(),
            CancellationToken.None);
    }

    private static async Task<IHost> StartSiloHostAsync(
        string bootstrapServers,
        string garnetConnectionString,
        TestTopology topology)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: topology.SiloPort,
                    gatewayPort: topology.GatewayPort,
                    serviceId: topology.ServiceId,
                    clusterId: topology.ClusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = garnetConnectionString;
                    options.StreamProviderName = topology.StreamProviderName;
                    options.ActorEventNamespace = topology.ActorEventNamespace;
                    options.QueueCount = 4;
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport();
            })
            .ConfigureServices(services =>
            {
                services.AddAevatarAgentKindRegistry(builder => builder
                    .Register<AlwaysSucceedOnNextAgent>()
                    .Register<ThrowOnceThenSucceedAgent>()
                    .Register<AlwaysFailOnNextAgent>());
                services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
                {
                    options.BootstrapServers = bootstrapServers;
                    options.TopicName = topology.TopicName;
                    options.ConsumerGroup = topology.ConsumerGroup;
                    options.TopicPartitionCount = 4;
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static string RequireKafkaBootstrapServers() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS.");

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    private static Offset ReadCommittedOffset(string bootstrapServers, TestTopology topology)
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = topology.ConsumerGroup,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        }).Build();
        var mapper = new KafkaQueuePartitionMapper(topology.StreamProviderName, 4);
        var partition = mapper.GetPartitionId(topology.ActorEventNamespace, topology.ActorId);
        var topicPartition = new TopicPartition(topology.TopicName, new Partition(partition));
        return consumer.Committed([topicPartition], TimeSpan.FromSeconds(10)).Single().Offset;
    }

    private static async Task WaitForCommittedOffsetAsync(
        string bootstrapServers,
        TestTopology topology,
        Offset expectedOffset,
        TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = topology.ConsumerGroup,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        }).Build();
        var mapper = new KafkaQueuePartitionMapper(topology.StreamProviderName, 4);
        var partition = mapper.GetPartitionId(topology.ActorEventNamespace, topology.ActorId);
        var topicPartition = new TopicPartition(topology.TopicName, new Partition(partition));
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var actual = consumer.Committed([topicPartition], TimeSpan.FromSeconds(1)).Single().Offset;
                if (actual == expectedOffset)
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for Kafka group '{topology.ConsumerGroup}' to commit offset {expectedOffset.Value}.");
        }
    }

    private static async Task<KafkaProviderBatchContainer> WaitForReceiverBatchAsync(
        KafkaProviderQueueAdapterReceiver receiver,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var messages = await receiver.GetQueueMessagesAsync(1);
                if (messages.FirstOrDefault() is KafkaProviderBatchContainer batch)
                    return batch;

                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for Kafka receiver batch delivery.");
        }
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TestTopology(
        string ActorId,
        string TopicName,
        string ConsumerGroup,
        string StreamProviderName,
        string ActorEventNamespace,
        string ClusterId,
        string ServiceId,
        int SiloPort,
        int GatewayPort)
    {
        public static TestTopology Create()
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new TestTopology(
                ActorId: $"redelivery-validator-actor-{suffix}",
                TopicName: $"aevatar-redelivery-validator-{suffix}",
                ConsumerGroup: $"aevatar-redelivery-validator-group-{suffix}",
                StreamProviderName: $"aevatar-redelivery-validator-provider-{suffix}",
                ActorEventNamespace: $"aevatar.redelivery.validator.{suffix}",
                ClusterId: $"aevatar-redelivery-validator-cluster-{suffix}",
                ServiceId: $"aevatar-redelivery-validator-service-{suffix}",
                SiloPort: ReserveTcpPort(),
                GatewayPort: ReserveTcpPort());
        }

        public TestTopology WithFreshPorts() =>
            this with
            {
                SiloPort = ReserveTcpPort(),
                GatewayPort = ReserveTcpPort(),
            };
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (var pair in overrides)
            {
                _originalValues[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _originalValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static class OnNextAttemptRecorder
    {
        private static readonly Lock SyncLock = new();
        private static Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private static Channel<(string EnvelopeId, int AttemptCount)> _events = CreateChannel();

        public static void Reset()
        {
            lock (SyncLock)
            {
                _attempts = new Dictionary<string, int>(StringComparer.Ordinal);
                _events = CreateChannel();
            }
        }

        public static int RecordAttempt(string envelopeId)
        {
            lock (SyncLock)
            {
                var count = _attempts.TryGetValue(envelopeId, out var existing) ? existing + 1 : 1;
                _attempts[envelopeId] = count;
                _events.Writer.TryWrite((envelopeId, count));
                return count;
            }
        }

        public static int CountAttempts(string envelopeId)
        {
            lock (SyncLock)
            {
                return _attempts.TryGetValue(envelopeId, out var existing) ? existing : 0;
            }
        }

        public static async Task WaitForAttemptsAsync(string envelopeId, int expectedAttempts, TimeSpan timeout)
        {
            Channel<(string EnvelopeId, int AttemptCount)> channel;
            lock (SyncLock)
            {
                if (_attempts.TryGetValue(envelopeId, out var existing) && existing >= expectedAttempts)
                    return;

                channel = _events;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var (observedId, count) = await channel.Reader.ReadAsync(cts.Token);
                    if (string.Equals(observedId, envelopeId, StringComparison.Ordinal) &&
                        count >= expectedAttempts)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                var observed = CountAttempts(envelopeId);
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for envelope '{envelopeId}' to reach {expectedAttempts} attempts. Observed {observed}.");
            }
        }

        private static Channel<(string EnvelopeId, int AttemptCount)> CreateChannel() =>
            Channel.CreateUnbounded<(string, int)>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    [GAgent("tests.always-succeed-on-next")]
    public sealed class AlwaysSucceedOnNextAgent : IAgent
    {
        public string Id => "always-succeed-on-next-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnNextAttemptRecorder.RecordAttempt(envelope.Id ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

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
    }

    [GAgent("tests.throw-once-then-succeed")]
    public sealed class ThrowOnceThenSucceedAgent : IAgent
    {
        public string Id => "throw-once-then-succeed-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = OnNextAttemptRecorder.RecordAttempt(envelope.Id ?? string.Empty);
            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    $"Intentional first-attempt failure for envelope '{envelope.Id}' to exercise persistent-provider redelivery.");
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

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
    }

    [GAgent("tests.always-fail-on-next")]
    public sealed class AlwaysFailOnNextAgent : IAgent
    {
        public string Id => "always-fail-on-next-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnNextAttemptRecorder.RecordAttempt(envelope.Id ?? string.Empty);
            throw new InvalidOperationException(
                $"Intentional terminal failure for envelope '{envelope.Id}'.");
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

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
    }
}
