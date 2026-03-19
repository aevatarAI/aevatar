using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class OrleansMassTransitRuntimeIntegrationTests
{
    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldInjectCompatibilityFailure_ForConfiguredEventTypeOnOldNode()
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

        var targetTypeUrl = Any.Pack(new StringValue { Value = "inject-me" }).TypeUrl;
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "old",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = targetTypeUrl,
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "2",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "0",
        });

        var logProbe = new CompatibilityFailureLogProbe();
        var host = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            siloPort,
            gatewayPort,
            loggerProvider: logProbe);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "inject-me" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };

            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);

            await logProbe.WaitForInjectedFailureAsync(TimeSpan.FromSeconds(20));
            await logProbe.WaitForRuntimeRetryScheduledAsync(TimeSpan.FromSeconds(20));

            var waitReceived = async () => await RecordingKafkaIntegrationAgent.WaitForEnvelopeAsync(TimeSpan.FromSeconds(8));
            await waitReceived.Should().ThrowAsync<TimeoutException>();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldKeepProcessingSubsequentEvents_AfterRetryExhaustedFailure()
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

        var failingTypeUrl = Any.Pack(new StringValue { Value = "retry-exhausted" }).TypeUrl;
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "old",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = failingTypeUrl,
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "0",
        });

        var logProbe = new CompatibilityFailureLogProbe();
        var host = await StartSiloHostAsync(
            bootstrapServers,
            topicName,
            consumerGroup,
            streamProviderName,
            actorEventNamespace,
            siloPort,
            gatewayPort,
            loggerProvider: logProbe);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentAsync(typeof(RecordingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var failingEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "retry-exhausted" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };
            await transport.PublishAsync(actorEventNamespace, actorId, failingEnvelope.ToByteArray(), CancellationToken.None);
            await logProbe.WaitForInjectedFailureAsync(TimeSpan.FromSeconds(20));

            await Task.Delay(300);

            var succeedingEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new Int32Value { Value = 7 }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };
            await transport.PublishAsync(actorEventNamespace, actorId, succeedingEnvelope.ToByteArray(), CancellationToken.None);

            var receivedEnvelope = await RecordingKafkaIntegrationAgent.WaitForEnvelopeAsync(
                envelope => envelope.Payload?.Is(Int32Value.Descriptor) == true,
                TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Unpack<Int32Value>().Value.Should().Be(7);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

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

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "ping" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
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

    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldDeliverSelfPublishedEnvelopeBackToSameActor()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-it-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        SelfPublishingKafkaIntegrationAgent.Reset();

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
            var initialized = await grain.InitializeAgentAsync(typeof(SelfPublishingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "trigger-self" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };

            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);

            var receivedEnvelope = await SelfPublishingKafkaIntegrationAgent.WaitForSelfEnvelopeAsync(TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Is(Int32Value.Descriptor).Should().BeTrue();
            receivedEnvelope.Payload.Unpack<Int32Value>().Value.Should().Be(42);
            receivedEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
            receivedEnvelope.Route.PublisherActorId.Should().Be(actorId);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldAutoRetryOnRuntimeException_AndEventuallySucceed()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-it-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        ThrowingKafkaIntegrationAgent.Reset();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "3",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
        });

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
            var initialized = await grain.InitializeAgentAsync(typeof(ThrowingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "fail-twice-then-ok" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };
            await transport.PublishAsync(actorEventNamespace, actorId, envelope.ToByteArray(), CancellationToken.None);

            var receivedEnvelope = await ThrowingKafkaIntegrationAgent.WaitForEnvelopeAsync(
                current => current.Payload?.Is(StringValue.Descriptor) == true &&
                           current.Payload.Unpack<StringValue>().Value == "fail-twice-then-ok",
                TimeSpan.FromSeconds(30));
            receivedEnvelope.Runtime!.Retry!.Attempt.Should().BeGreaterThan(0);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldKeepProcessingSubsequentEvents_AfterRuntimeRetryExhaustedFailure()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-it-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        ThrowingKafkaIntegrationAgent.Reset();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "0",
        });

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
            var initialized = await grain.InitializeAgentAsync(typeof(ThrowingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var failingEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "always-fail" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };
            await transport.PublishAsync(actorEventNamespace, actorId, failingEnvelope.ToByteArray(), CancellationToken.None);
            await Task.Delay(500);

            var succeedingEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "ok" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };
            await transport.PublishAsync(actorEventNamespace, actorId, succeedingEnvelope.ToByteArray(), CancellationToken.None);

            var receivedEnvelope = await ThrowingKafkaIntegrationAgent.WaitForEnvelopeAsync(
                current => current.Payload?.Is(StringValue.Descriptor) == true &&
                           current.Payload.Unpack<StringValue>().Value == "ok",
                TimeSpan.FromSeconds(30));
            receivedEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("ok");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [KafkaIntegrationFact]
    public async Task KafkaTransport_ShouldDeduplicateOriginalEnvelope_AndStillAllowRuntimeRetrySuccess()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var topicName = $"aevatar-orleans-it-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-orleans-it-group-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-orleans-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.orleans.it.{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        ThrowingKafkaIntegrationAgent.Reset();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "3",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
        });

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
            var initialized = await grain.InitializeAgentAsync(typeof(ThrowingKafkaIntegrationAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            var transport = host.Services.GetRequiredService<IMassTransitEnvelopeTransport>();
            var originalEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new StringValue { Value = "fail-once-then-ok" }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            };

            await transport.PublishAsync(actorEventNamespace, actorId, originalEnvelope.ToByteArray(), CancellationToken.None);
            await transport.PublishAsync(actorEventNamespace, actorId, originalEnvelope.ToByteArray(), CancellationToken.None);

            var receivedEnvelope = await ThrowingKafkaIntegrationAgent.WaitForEnvelopeAsync(
                current => current.Payload?.Is(StringValue.Descriptor) == true &&
                           current.Payload.Unpack<StringValue>().Value == "fail-once-then-ok",
                TimeSpan.FromSeconds(30));
            receivedEnvelope.Runtime!.Retry!.OriginEventId.Should().Be(originalEnvelope.Id);

            var processedCount = ThrowingKafkaIntegrationAgent.GetProcessedCount(originalEnvelope.Id);
            processedCount.Should().Be(1);
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
        string persistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory,
        string? garnetConnectionString = null,
        ILoggerProvider? loggerProvider = null,
        int queueCount = 1,
        int topicPartitionCount = 1)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: serviceId ?? $"aevatar-orleans-it-service-{Guid.NewGuid():N}",
                    clusterId: clusterId ?? $"aevatar-orleans-it-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendMassTransitAdapter;
                    options.PersistenceBackend = persistenceBackend;
                    options.GarnetConnectionString = garnetConnectionString ?? string.Empty;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.QueueCount = queueCount;
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
            })
            .ConfigureLogging(logging =>
            {
                if (loggerProvider != null)
                    logging.AddProvider(loggerProvider);
            })
            .ConfigureServices(services =>
            {
                services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
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

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RequireKafkaBootstrapServers() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS.");

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues;

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> overrides)
        {
            _originalValues = new Dictionary<string, string?>(StringComparer.Ordinal);
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

    private sealed class CompatibilityFailureLogProbe : ILoggerProvider, ILogger
    {
        private readonly TaskCompletionSource<bool> _injectedFailureDetected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _runtimeRetryScheduledDetected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName)
        {
            _ = categoryName;
            return this;
        }

        public void Dispose()
        {
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            _ = exception;
            var message = formatter(state, exception);
            if (message.Contains("Injected compatibility failure", StringComparison.Ordinal))
                _injectedFailureDetected.TrySetResult(true);
            if (message.Contains("Runtime envelope retry scheduled", StringComparison.Ordinal))
                _runtimeRetryScheduledDetected.TrySetResult(true);
        }

        public async Task WaitForInjectedFailureAsync(TimeSpan timeout)
        {
            try
            {
                await _injectedFailureDetected.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for injected compatibility failure log.");
            }
        }

        public async Task WaitForRuntimeRetryScheduledAsync(TimeSpan timeout)
        {
            try
            {
                await _runtimeRetryScheduledDetected.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for runtime retry scheduling log.");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private static class LastFailedEnvelopeHolder
    {
        private static readonly Lock SyncLock = new();
        private static EventEnvelope? _failedEnvelope;

        public static void Set(EventEnvelope envelope)
        {
            lock (SyncLock)
            {
                _failedEnvelope = envelope.Clone();
            }
        }

        public static EventEnvelope? GetAndClear()
        {
            lock (SyncLock)
            {
                var envelope = _failedEnvelope?.Clone();
                _failedEnvelope = null;
                return envelope;
            }
        }
    }

    public sealed class RecordingKafkaIntegrationAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _receivedEnvelopes = CreateChannel();

        public string Id => "recording-kafka-integration-agent";

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
                _receivedEnvelopes = CreateChannel();
            }
        }

        public static async Task<EventEnvelope> WaitForEnvelopeAsync(TimeSpan timeout)
        {
            return await WaitForEnvelopeAsync(_ => true, timeout);
        }

        public static async Task<EventEnvelope> WaitForEnvelopeAsync(
            Func<EventEnvelope, bool> predicate,
            TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _receivedEnvelopes;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var envelope = await channel.Reader.ReadAsync(cts.Token);
                    if (predicate(envelope))
                        return envelope;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for Kafka envelope.");
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
                throw new TimeoutException($"Timed out after {timeout} waiting for {expectedCount} Kafka envelopes.");
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

    public sealed class ThrowingKafkaIntegrationAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _receivedEnvelopes = CreateChannel();
        private static readonly Dictionary<string, int> AttemptsByOriginId = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ProcessedByOriginId = new(StringComparer.Ordinal);

        public string Id => "throwing-kafka-integration-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var textValue = envelope.Payload?.Is(StringValue.Descriptor) == true
                ? envelope.Payload.Unpack<StringValue>().Value
                : string.Empty;

            if (string.Equals(textValue, "always-fail", StringComparison.Ordinal))
                throw new InvalidOperationException("always-fail");

            if (string.Equals(textValue, "fail-twice-then-ok", StringComparison.Ordinal))
            {
                var originId = ResolveOriginId(envelope);
                var attempt = IncrementAttempt(originId);
                if (attempt <= 2)
                    throw new InvalidOperationException($"flaky-failure-{attempt}");
            }

            if (string.Equals(textValue, "fail-once-then-ok", StringComparison.Ordinal))
            {
                var originId = ResolveOriginId(envelope);
                var attempt = IncrementAttempt(originId);
                if (attempt <= 1)
                    throw new InvalidOperationException($"flaky-failure-{attempt}");
            }

            var processedOriginId = ResolveOriginId(envelope);
            IncrementProcessed(processedOriginId);

            lock (SyncLock)
            {
                _receivedEnvelopes.Writer.TryWrite(envelope.Clone());
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("throwing-kafka-integration-agent");

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
                AttemptsByOriginId.Clear();
                ProcessedByOriginId.Clear();
            }
        }

        public static int GetProcessedCount(string originId)
        {
            lock (SyncLock)
            {
                return ProcessedByOriginId.TryGetValue(originId, out var count) ? count : 0;
            }
        }

        public static async Task<EventEnvelope> WaitForEnvelopeAsync(
            Func<EventEnvelope, bool> predicate,
            TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _receivedEnvelopes;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var envelope = await channel.Reader.ReadAsync(cts.Token);
                    if (predicate(envelope))
                        return envelope;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for Kafka envelope.");
            }
        }

        private static Channel<EventEnvelope> CreateChannel() =>
            Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private static int IncrementAttempt(string originId)
        {
            lock (SyncLock)
            {
                AttemptsByOriginId.TryGetValue(originId, out var current);
                var next = current + 1;
                AttemptsByOriginId[originId] = next;
                return next;
            }
        }

        private static void IncrementProcessed(string originId)
        {
            lock (SyncLock)
            {
                ProcessedByOriginId.TryGetValue(originId, out var current);
                ProcessedByOriginId[originId] = current + 1;
            }
        }

        private static string ResolveOriginId(EventEnvelope envelope)
        {
            var originId = envelope.Runtime?.Retry?.OriginEventId;
            if (!string.IsNullOrWhiteSpace(originId))
                return originId;
            return string.IsNullOrWhiteSpace(envelope.Id) ? Guid.NewGuid().ToString("N") : envelope.Id;
        }
    }

    public sealed class SelfPublishingKafkaIntegrationAgent : GAgentBase
    {
        private static readonly Lock SyncLock = new();
        private static Channel<EventEnvelope> _selfEnvelopes = CreateChannel();

        [EventHandler]
        public async Task HandleStringValue(StringValue value)
        {
            if (!string.Equals(value.Value, "trigger-self", StringComparison.Ordinal))
                return;

            await PublishAsync(new Int32Value { Value = 42 }, TopologyAudience.Self);
        }

        [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
        public Task HandleSelfInt32Value(Int32Value value)
        {
            if (value.Value != 42 || ActiveInboundEnvelope == null)
                return Task.CompletedTask;

            lock (SyncLock)
            {
                _selfEnvelopes.Writer.TryWrite(ActiveInboundEnvelope.Clone());
            }

            return Task.CompletedTask;
        }

        public override Task<string> GetDescriptionAsync() =>
            Task.FromResult("self-publishing-kafka-integration-agent");

        public static void Reset()
        {
            lock (SyncLock)
            {
                _selfEnvelopes = CreateChannel();
            }
        }

        public static async Task<EventEnvelope> WaitForSelfEnvelopeAsync(TimeSpan timeout)
        {
            Channel<EventEnvelope> channel;
            lock (SyncLock)
            {
                channel = _selfEnvelopes;
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await channel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for self-published envelope.");
            }
        }

        private static Channel<EventEnvelope> CreateChannel() =>
            Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
            });
    }
}
