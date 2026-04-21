using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
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

/// <summary>
/// Validation harness for the Orleans Kafka persistent stream provider
/// "throw-vs-return" redelivery contract (Channel RFC §9.5.6).
///
/// Asserts that the Kafka-backed Orleans persistent stream provider:
///   1. Does NOT redeliver an envelope when the subscriber's OnNextAsync returns normally.
///   2. DOES redeliver an envelope when the subscriber's OnNextAsync throws and the
///      throw is propagated (envelope.Runtime.Dispatch.PropagateFailure = true).
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

        var host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentAsync(typeof(AlwaysSucceedOnNextAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            var envelopeId = Guid.NewGuid().ToString("N");
            await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: false);

            await OnNextAttemptRecorder.WaitForAttemptsAsync(envelopeId, expectedAttempts: 1, RedeliveryTimeout);
            await Task.Delay(NoRedeliveryQuietPeriod);

            OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(
                1,
                "a persistent stream provider must NOT redeliver an envelope whose subscriber returned normally");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenOnNextAsyncThrows_RedeliversMessage()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        OnNextAttemptRecorder.Reset();

        var host = await StartSiloHostAsync(bootstrapServers, garnetConnectionString, topology);
        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentAsync(typeof(ThrowOnceThenSucceedAgent).AssemblyQualifiedName!))
                .Should().BeTrue();

            var envelopeId = Guid.NewGuid().ToString("N");
            await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: true);

            await OnNextAttemptRecorder.WaitForAttemptsAsync(envelopeId, expectedAttempts: 2, RedeliveryTimeout);

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
}
