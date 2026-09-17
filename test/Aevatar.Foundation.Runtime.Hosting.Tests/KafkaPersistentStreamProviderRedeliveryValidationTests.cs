using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
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
///      throw is propagated (envelope.Runtime.Dispatch.PropagateFailure = true), and the
///      repeated delivery reaches the authoritative actor again.
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
    private const string PhaseBTurnoverAgentKind = "tests.phase-b-forwarded-status-redelivery";
    private const string CrashWorkerEnvironmentVariable = "AEVATAR_KAFKA_ACK_CRASH_WORKER";
    private const string CrashWorkerEnvelopeIdEnvironmentVariable = "AEVATAR_KAFKA_ACK_CRASH_ENVELOPE_ID";
    private const string CrashWorkerHandlerMarkerEnvironmentVariable = "AEVATAR_KAFKA_ACK_CRASH_HANDLER_MARKER";
    private const string CrashWorkerAckMarkerEnvironmentVariable = "AEVATAR_KAFKA_ACK_CRASH_ACK_MARKER";

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
    public Task KafkaPersistentProvider_PhaseBTurnoverThenForwardedConflict_ShouldRedeliverUntilSuccess() =>
        AssertPhaseBTurnoverThenForwardedStatusRejectionAsync(ProjectionWriteDisposition.Conflict);

    [KafkaGarnetIntegrationFact]
    public Task KafkaPersistentProvider_PhaseBTurnoverThenForwardedGap_ShouldRedeliverUntilSuccess() =>
        AssertPhaseBTurnoverThenForwardedStatusRejectionAsync(ProjectionWriteDisposition.Gap);

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
        var expectedPartition = mapper.GetPartitionId(topology.ActorEventNamespace, topology.ActorId);
        var receiverBufferDepths = new ConcurrentQueue<(
            long Value,
            string? Provider,
            string? Topic,
            int? Partition)>();
        using var metricListener = new MeterListener();
        metricListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == KafkaTransportMetrics.MeterName &&
                instrument.Name == "aevatar.kafka.receiver.buffer_depth")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        metricListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? provider = null;
            string? topic = null;
            int? partition = null;
            foreach (var tag in tags)
            {
                if (tag.Key == KafkaTransportMetrics.ProviderTag)
                    provider = tag.Value?.ToString();
                else if (tag.Key == KafkaTransportMetrics.TopicTag)
                    topic = tag.Value?.ToString();
                else if (tag.Key == KafkaTransportMetrics.PartitionTag && tag.Value is int partitionValue)
                    partition = partitionValue;
            }

            receiverBufferDepths.Enqueue((value, provider, topic, partition));
        });
        metricListener.Start();
        await using var producer = new KafkaProviderProducer(options, mapper);
        KafkaProviderQueueAdapterReceiver? receiver = null;

        try
        {
            var queueId = mapper.GetQueueForStream(
                StreamId.Create(topology.ActorEventNamespace, topology.ActorId));
            receiver = new KafkaProviderQueueAdapterReceiver(
                queueId,
                topology.StreamProviderName,
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
            receiverBufferDepths.Should().Contain(measurement =>
                measurement.Value == 1 &&
                measurement.Provider == topology.StreamProviderName &&
                measurement.Topic == topology.TopicName &&
                measurement.Partition == expectedPartition,
                "enqueueing a Kafka batch must report the receiver's buffered message count");
            await receiver.Shutdown(TimeSpan.FromSeconds(10));
            receiver = null;

            ReadCommittedOffset(bootstrapServers, topology).Should().NotBe(
                new Offset(firstDelivery.KafkaOffset + 1),
                "returning a batch from the receiver must not commit before MessagesDeliveredAsync");

            receiver = new KafkaProviderQueueAdapterReceiver(
                queueId,
                topology.StreamProviderName,
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

    [KafkaGarnetIntegrationFact]
    public async Task KafkaPersistentProvider_WhenProcessExitsAfterHandlerSuccessBeforeMessagesDelivered_RedeliversAfterRestart()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        var envelopeId = Guid.NewGuid().ToString("N");
        var markerDirectory = Path.Combine(
            Path.GetTempPath(),
            $"aevatar-kafka-ack-crash-{Guid.NewGuid():N}");
        var handlerMarkerPath = Path.Combine(markerDirectory, "handler-succeeded");
        var ackMarkerPath = Path.Combine(markerDirectory, "messages-delivered-entered");
        Directory.CreateDirectory(markerDirectory);

        try
        {
            using var worker = StartCrashWorkerProcess(
                bootstrapServers,
                garnetConnectionString,
                topology,
                envelopeId,
                handlerMarkerPath,
                ackMarkerPath);
            var workerStandardOutput = worker.StandardOutput.ReadToEndAsync();
            var workerStandardError = worker.StandardError.ReadToEndAsync();
            using var workerTimeout = new CancellationTokenSource(RedeliveryTimeout);
            try
            {
                await worker.WaitForExitAsync(workerTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                throw new TimeoutException(
                    $"Crash worker did not exit within {RedeliveryTimeout}.\n" +
                    await ReadWorkerOutputAsync(workerStandardOutput, workerStandardError));
            }

            var workerOutput = await ReadWorkerOutputAsync(workerStandardOutput, workerStandardError);
            worker.ExitCode.Should().NotBe(
                0,
                $"the isolated silo must terminate at the pre-ACK crash point\n{workerOutput}");
            File.ReadAllText(handlerMarkerPath).Should().Be(
                envelopeId,
                "the real RuntimeActorGrain handler must finish before the process is terminated");
            File.ReadAllText(ackMarkerPath).Should().Be(
                envelopeId,
                "the process must terminate exactly when Orleans enters MessagesDeliveredAsync");
            ReadCommittedOffset(bootstrapServers, topology).Should().NotBe(
                new Offset(1),
                "the intercepted MessagesDeliveredAsync call must not advance the Kafka group offset");

            OnNextAttemptRecorder.Reset();
            var restartedTopology = topology.WithFreshPorts();
            var host = await StartSiloHostAsync(
                bootstrapServers,
                garnetConnectionString,
                restartedTopology);
            try
            {
                await OnNextAttemptRecorder.WaitForAttemptsAsync(
                    envelopeId,
                    expectedAttempts: 1,
                    RedeliveryTimeout);
                await WaitForCommittedOffsetAsync(
                    bootstrapServers,
                    restartedTopology,
                    new Offset(1),
                    RedeliveryTimeout);

                OnNextAttemptRecorder.CountAttempts(envelopeId).Should().Be(
                    1,
                    "the uncommitted Kafka record must traverse observer -> RuntimeActorGrain -> handler after restart");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        finally
        {
            Directory.Delete(markerDirectory, recursive: true);
        }
    }

    [KafkaAckCrashWorkerFact]
    public async Task KafkaPersistentProvider_AckCrashWorker()
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.FromCrashWorkerEnvironment();
        var envelopeId = RequireEnvironmentVariable(CrashWorkerEnvelopeIdEnvironmentVariable);
        var host = await StartSiloHostAsync(
            bootstrapServers,
            garnetConnectionString,
            topology,
            crashBeforeAcknowledgement: true);

        var grain = host.Services.GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeActorGrain>(topology.ActorId);
        (await grain.InitializeAgentByKindAsync("tests.always-succeed-on-next"))
            .Should().BeTrue();
        await PublishEnvelopeAsync(host, topology, envelopeId, propagateFailure: false);

        await Task.Delay(RedeliveryTimeout);
        throw new TimeoutException(
            "The Kafka ACK crash worker did not reach MessagesDeliveredAsync after handler success.");
    }

    private static async Task AssertPhaseBTurnoverThenForwardedStatusRejectionAsync(
        ProjectionWriteDisposition disposition)
    {
        var bootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var topology = TestTopology.Create();
        var sourceActorId = $"phase-b-source-{Guid.NewGuid():N}";
        var fixture = new PhaseBTurnoverFixture();
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "0",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(
            bootstrapServers,
            garnetConnectionString,
            topology,
            phaseBTurnoverFixture: fixture);
        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(topology.ActorId);
            (await grain.InitializeAgentByKindAsync(PhaseBTurnoverAgentKind))
                .Should().BeTrue();

            var initialConstructions = fixture.Recorder.ConstructionContexts;
            initialConstructions.Should().ContainSingle();
            initialConstructions[0].StateSchemaVersion.Should().Be(
                0,
                "a denied V3 admission must leave the active actor on its durable schema-zero row");
            fixture.Recorder.HandlerAttempts.Should().BeEmpty();

            fixture.AdmissionReader.Open();
            var forwarded = CreateForwardedObserverEnvelope(
                sourceActorId,
                topology.ActorId,
                disposition);
            await PublishEnvelopeAsync(host, topology, forwarded);

            await fixture.Recorder.WaitForSchemaZeroDeactivationAsync(RedeliveryTimeout);
            await fixture.Recorder.WaitForFirstHandlerAttemptAsync(RedeliveryTimeout);
            ReadCommittedOffset(bootstrapServers, topology).Should().NotBe(
                new Offset(1),
                "turnover and an in-flight migrated handler are not provider acknowledgement");

            var firstAttempt = fixture.Recorder.HandlerAttempts.Should().ContainSingle().Which;
            firstAttempt.StateSchemaVersion.Should().Be(
                1,
                "the schema-zero activation must turn over before entering the handler");
            AssertForwardedIdentity(firstAttempt.Envelope, forwarded, sourceActorId, topology.ActorId);

            fixture.Recorder.AllowFirstRejection();
            await fixture.Recorder.WaitForSecondHandlerAttemptAsync(RedeliveryTimeout);
            ReadCommittedOffset(bootstrapServers, topology).Should().NotBe(
                new Offset(1),
                $"a retryable {disposition} rejection must leave the Kafka message unacknowledged");

            fixture.Recorder.AllowFinalSuccess();
            var delivered = await fixture.Recorder.WaitForSuccessAsync(RedeliveryTimeout);
            await WaitForCommittedOffsetAsync(
                bootstrapServers,
                topology,
                new Offset(1),
                RedeliveryTimeout);

            AssertForwardedIdentity(delivered, forwarded, sourceActorId, topology.ActorId);
            var attempts = fixture.Recorder.HandlerAttempts;
            attempts.Should().HaveCountGreaterThanOrEqualTo(2);
            attempts.Should().OnlyContain(attempt =>
                attempt.StateSchemaVersion == 1 && attempt.Envelope.Equals(forwarded));

            var constructions = fixture.Recorder.ConstructionContexts;
            constructions[0].StateSchemaVersion.Should().Be(0);
            constructions.Skip(1).Should().NotBeEmpty();
            constructions.Skip(1).Should().OnlyContain(context => context.StateSchemaVersion == 1);
            var adoption = constructions
                .First(context => context.StateSchemaVersion == 1)
                .AdoptionReceipts
                .Should()
                .ContainSingle()
                .Which;
            adoption.StateSchemaVersion.Should().Be(1);
            adoption.RequiredCapability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
            adoption.RequiredContractId.Should().Be(
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
            adoption.RequiredContractVersion.Should().Be(
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
            adoption.EvidenceStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        }
        finally
        {
            fixture.Recorder.AllowFirstRejection();
            fixture.Recorder.AllowFinalSuccess();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static EventEnvelope CreateForwardedObserverEnvelope(
        string sourceActorId,
        string targetActorId,
        ProjectionWriteDisposition disposition)
    {
        var published = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new StringValue { Value = disposition.ToString() }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                sourceActorId,
                ObserverAudience.CommittedFacts),
            Runtime = new EnvelopeRuntime { SourceActorId = sourceActorId },
        };
        return StreamForwardingRules.BuildForwardedEnvelope(
            published,
            sourceActorId,
            targetActorId,
            StreamForwardingMode.HandleThenForward);
    }

    private static void AssertForwardedIdentity(
        EventEnvelope actual,
        EventEnvelope expected,
        string sourceActorId,
        string targetActorId)
    {
        actual.Equals(expected).Should().BeTrue("provider redelivery must preserve the exact envelope");
        actual.Id.Should().Be(expected.Id);
        actual.Route.IsObserverPublication().Should().BeTrue();
        actual.Route.PublisherActorId.Should().Be(sourceActorId);
        actual.Runtime.SourceActorId.Should().Be(sourceActorId);
        StreamForwardingEnvelopeState.GetSourceStreamId(actual).Should().Be(sourceActorId);
        StreamForwardingEnvelopeState.GetTargetStreamId(actual).Should().Be(targetActorId);
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

    private static async Task PublishEnvelopeAsync(
        IHost host,
        TestTopology topology,
        EventEnvelope envelope)
    {
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
        TestTopology topology,
        bool crashBeforeAcknowledgement = false,
        PhaseBTurnoverFixture? phaseBTurnoverFixture = null)
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
                services.AddAevatarAgentKindRegistry(builder =>
                {
                    builder
                        .Register<AlwaysSucceedOnNextAgent>()
                        .Register<ThrowOnceThenSucceedAgent>()
                        .Register<AlwaysFailOnNextAgent>();
                    if (phaseBTurnoverFixture != null)
                    {
                        builder.Register(new AgentRegistration(
                            PhaseBTurnoverAgentKind,
                            typeof(PhaseBTurnoverRedeliveryAgent),
                            typeof(EventEnvelope),
                            StateSchemaVersion: 1,
                            StateMigrationTypes: [typeof(PhaseBTurnoverStateV0ToV1Migration)]));
                    }
                });
                services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
                {
                    options.BootstrapServers = bootstrapServers;
                    options.TopicName = topology.TopicName;
                    options.ConsumerGroup = topology.ConsumerGroup;
                    options.TopicPartitionCount = 4;
                });
                if (phaseBTurnoverFixture != null)
                {
                    services.AddSingleton(phaseBTurnoverFixture.Recorder);
                    services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetCapabilityAdmissionReader>(
                        phaseBTurnoverFixture.AdmissionReader));
                    services.Replace(ServiceDescriptor.Singleton<IRuntimeLocalMembershipIdentityReader>(
                        new PhaseBMembershipReader()));
                }
                if (crashBeforeAcknowledgement)
                {
                    services.RemoveAll<IQueueAdapterFactory>();
                    services.AddSingleton<IQueueAdapterFactory, CrashBeforeAcknowledgementQueueAdapterFactory>();
                }
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

    private static string RequireEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing {name}.");

    private static Process StartCrashWorkerProcess(
        string bootstrapServers,
        string garnetConnectionString,
        TestTopology topology,
        string envelopeId,
        string handlerMarkerPath,
        string ackMarkerPath)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(KafkaPersistentStreamProviderRedeliveryValidationTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            $"--Tests:{typeof(KafkaPersistentStreamProviderRedeliveryValidationTests).FullName}.KafkaPersistentProvider_AckCrashWorker");
        startInfo.Environment["AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS"] = bootstrapServers;
        startInfo.Environment["AEVATAR_TEST_GARNET_CONNECTION_STRING"] = garnetConnectionString;
        startInfo.Environment[CrashWorkerEnvironmentVariable] = "1";
        startInfo.Environment[CrashWorkerEnvelopeIdEnvironmentVariable] = envelopeId;
        startInfo.Environment[CrashWorkerHandlerMarkerEnvironmentVariable] = handlerMarkerPath;
        startInfo.Environment[CrashWorkerAckMarkerEnvironmentVariable] = ackMarkerPath;
        topology.WriteCrashWorkerEnvironment(startInfo.Environment);

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start isolated Kafka ACK crash worker.");
    }

    private static async Task<string> ReadWorkerOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        return $"stdout:\n{await standardOutput}\nstderr:\n{await standardError}";
    }

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

        public static TestTopology FromCrashWorkerEnvironment() =>
            new(
                ActorId: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_ACTOR_ID"),
                TopicName: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_TOPIC"),
                ConsumerGroup: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_CONSUMER_GROUP"),
                StreamProviderName: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_STREAM_PROVIDER"),
                ActorEventNamespace: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_STREAM_NAMESPACE"),
                ClusterId: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_CLUSTER_ID"),
                ServiceId: RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_SERVICE_ID"),
                SiloPort: int.Parse(RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_SILO_PORT")),
                GatewayPort: int.Parse(RequireEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_GATEWAY_PORT")));

        public void WriteCrashWorkerEnvironment(IDictionary<string, string?> environment)
        {
            environment["AEVATAR_KAFKA_ACK_CRASH_ACTOR_ID"] = ActorId;
            environment["AEVATAR_KAFKA_ACK_CRASH_TOPIC"] = TopicName;
            environment["AEVATAR_KAFKA_ACK_CRASH_CONSUMER_GROUP"] = ConsumerGroup;
            environment["AEVATAR_KAFKA_ACK_CRASH_STREAM_PROVIDER"] = StreamProviderName;
            environment["AEVATAR_KAFKA_ACK_CRASH_STREAM_NAMESPACE"] = ActorEventNamespace;
            environment["AEVATAR_KAFKA_ACK_CRASH_CLUSTER_ID"] = ClusterId;
            environment["AEVATAR_KAFKA_ACK_CRASH_SERVICE_ID"] = ServiceId;
            environment["AEVATAR_KAFKA_ACK_CRASH_SILO_PORT"] = SiloPort.ToString();
            environment["AEVATAR_KAFKA_ACK_CRASH_GATEWAY_PORT"] = GatewayPort.ToString();
        }
    }

    private sealed class CrashBeforeAcknowledgementQueueAdapterFactory : IQueueAdapterFactory
    {
        private readonly KafkaProviderQueueAdapterFactory _inner;

        public CrashBeforeAcknowledgementQueueAdapterFactory(
            AevatarOrleansRuntimeOptions runtimeOptions,
            KafkaProviderProducer transport,
            KafkaProviderTransportOptions transportOptions,
            KafkaQueuePartitionMapper mapper,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
        {
            _inner = new KafkaProviderQueueAdapterFactory(
                runtimeOptions,
                transport,
                transportOptions,
                mapper,
                loggerFactory);
        }

        public async Task<IQueueAdapter> CreateAdapter() =>
            new CrashBeforeAcknowledgementQueueAdapter(await _inner.CreateAdapter());

        public IQueueAdapterCache GetQueueAdapterCache() => _inner.GetQueueAdapterCache();

        public IStreamQueueMapper GetStreamQueueMapper() => _inner.GetStreamQueueMapper();

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
            _inner.GetDeliveryFailureHandler(queueId);
    }

    private sealed class CrashBeforeAcknowledgementQueueAdapter(IQueueAdapter inner) : IQueueAdapter
    {
        public string Name => inner.Name;

        public bool IsRewindable => inner.IsRewindable;

        public StreamProviderDirection Direction => inner.Direction;

        public Task QueueMessageBatchAsync<T>(
            StreamId streamId,
            IEnumerable<T> events,
            StreamSequenceToken token,
            Dictionary<string, object> requestContext) =>
            inner.QueueMessageBatchAsync(streamId, events, token, requestContext);

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
            new CrashBeforeAcknowledgementQueueAdapterReceiver(inner.CreateReceiver(queueId));
    }

    private sealed class CrashBeforeAcknowledgementQueueAdapterReceiver(IQueueAdapterReceiver inner)
        : IQueueAdapterReceiver
    {
        public Task Initialize(TimeSpan timeout) => inner.Initialize(timeout);

        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) =>
            inner.GetQueueMessagesAsync(maxCount);

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            if (messages.Count == 0)
                return inner.MessagesDeliveredAsync(messages);

            var envelopeId = RequireEnvironmentVariable(CrashWorkerEnvelopeIdEnvironmentVariable);
            var handlerMarkerPath = RequireEnvironmentVariable(CrashWorkerHandlerMarkerEnvironmentVariable);
            if (!File.Exists(handlerMarkerPath) ||
                !string.Equals(File.ReadAllText(handlerMarkerPath), envelopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Orleans entered MessagesDeliveredAsync before the expected RuntimeActorGrain handler completed.");
            }

            File.WriteAllText(
                RequireEnvironmentVariable(CrashWorkerAckMarkerEnvironmentVariable),
                envelopeId);
            Environment.FailFast(
                "Intentional test-only process exit after handler success and before Kafka offset acknowledgement.");
            return Task.CompletedTask;
        }

        public Task Shutdown(TimeSpan timeout) => inner.Shutdown(timeout);
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

    private sealed class PhaseBTurnoverFixture
    {
        public PhaseBTurnoverRecorder Recorder { get; } = new();

        public PhaseBAdmissionReader AdmissionReader { get; } = new();
    }

    private sealed class PhaseBAdmissionReader : IRuntimeFleetCapabilityAdmissionReader
    {
        private readonly Lock _lock = new();
        private RuntimeFleetCapabilityAdmission? _current;

        public void Open()
        {
            var now = DateTimeOffset.UtcNow;
            var admission = new RuntimeFleetCapabilityAdmission
            {
                Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
                Status = RuntimeFleetCapabilityGateStatus.Open,
                AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                AuthorityStateVersion = 9,
                CapabilityEpoch = 3,
                MembershipEpoch = 7,
                DeploymentRevision = "revision-a",
                MinimumReaderContractVersion =
                    RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
                MembershipObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddSeconds(-5)),
                MembershipValidUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(5)),
                ActiveMemberCount = 1,
                ConfirmedMemberCount = 1,
                MembershipDigest = "digest-a",
                ContractId =
                    RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            };
            admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
            {
                MemberId = "member-a",
                Incarnation = "inc-a",
            });

            lock (_lock)
            {
                _current = admission;
            }
        }

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult<RuntimeFleetCapabilityAdmission?>(
                    _current?.Capability == capability ? _current.Clone() : null);
            }
        }
    }

    private sealed class PhaseBMembershipReader : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(
                new RuntimeLocalMembershipIdentity(
                    7,
                    "digest-a",
                    "revision-a",
                    "member-a",
                    "inc-a"));
        }
    }

    public sealed record PhaseBHandlerAttempt(
        int StateSchemaVersion,
        EventEnvelope Envelope);

    public sealed class PhaseBTurnoverRecorder
    {
        private readonly ConcurrentQueue<RuntimeActorStateSchemaContext> _constructions = new();
        private readonly ConcurrentQueue<RuntimeActorStateSchemaContext> _activations = new();
        private readonly ConcurrentQueue<RuntimeActorStateSchemaContext> _deactivations = new();
        private readonly ConcurrentQueue<PhaseBHandlerAttempt> _handlerAttempts = new();
        private readonly TaskCompletionSource<bool> _schemaZeroDeactivated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstHandlerAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondHandlerAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowFirstRejection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowFinalSuccess =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<EventEnvelope> _success =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _handlerAttemptCount;

        public IReadOnlyList<RuntimeActorStateSchemaContext> ConstructionContexts =>
            [.. _constructions];

        public IReadOnlyList<RuntimeActorStateSchemaContext> ActivationContexts =>
            [.. _activations];

        public IReadOnlyList<RuntimeActorStateSchemaContext> DeactivationContexts =>
            [.. _deactivations];

        public IReadOnlyList<PhaseBHandlerAttempt> HandlerAttempts =>
            [.. _handlerAttempts];

        public void RecordConstruction(RuntimeActorStateSchemaContext? context) =>
            _constructions.Enqueue(CloneContext(context));

        public void RecordActivation(RuntimeActorStateSchemaContext? context) =>
            _activations.Enqueue(CloneContext(context));

        public void RecordDeactivation(RuntimeActorStateSchemaContext? context)
        {
            var snapshot = CloneContext(context);
            _deactivations.Enqueue(snapshot);
            if (snapshot.StateSchemaVersion == 0)
                _schemaZeroDeactivated.TrySetResult(true);
        }

        public int RecordHandlerAttempt(RuntimeActorStateSchemaContext? context, EventEnvelope envelope)
        {
            var snapshot = CloneContext(context);
            var attempt = Interlocked.Increment(ref _handlerAttemptCount);
            _handlerAttempts.Enqueue(new PhaseBHandlerAttempt(
                snapshot.StateSchemaVersion,
                envelope.Clone()));
            if (attempt == 1)
                _firstHandlerAttempted.TrySetResult(true);
            else if (attempt == 2)
                _secondHandlerAttempted.TrySetResult(true);
            return attempt;
        }

        public Task WaitForSchemaZeroDeactivationAsync(TimeSpan timeout) =>
            _schemaZeroDeactivated.Task.WaitAsync(timeout);

        public Task WaitForFirstHandlerAttemptAsync(TimeSpan timeout) =>
            _firstHandlerAttempted.Task.WaitAsync(timeout);

        public Task WaitForSecondHandlerAttemptAsync(TimeSpan timeout) =>
            _secondHandlerAttempted.Task.WaitAsync(timeout);

        public Task WaitUntilFirstRejectionAllowedAsync() => _allowFirstRejection.Task;

        public Task WaitUntilFinalSuccessAllowedAsync() => _allowFinalSuccess.Task;

        public void AllowFirstRejection() => _allowFirstRejection.TrySetResult(true);

        public void AllowFinalSuccess() => _allowFinalSuccess.TrySetResult(true);

        public void RecordSuccess(EventEnvelope envelope) => _success.TrySetResult(envelope.Clone());

        public Task<EventEnvelope> WaitForSuccessAsync(TimeSpan timeout) =>
            _success.Task.WaitAsync(timeout);

        private static RuntimeActorStateSchemaContext CloneContext(
            RuntimeActorStateSchemaContext? context) =>
            context == null
                ? new RuntimeActorStateSchemaContext(string.Empty, -1, [])
                : new RuntimeActorStateSchemaContext(
                    context.AgentKind,
                    context.StateSchemaVersion,
                    context.AdoptionReceipts.Select(static receipt => receipt.Clone()).ToArray());
    }

    public sealed class PhaseBTurnoverRedeliveryAgent : IAgent
    {
        private readonly PhaseBTurnoverRecorder _recorder;
        private readonly IRuntimeActorStateSchemaContextReader _schemaContext;

        public PhaseBTurnoverRedeliveryAgent(
            PhaseBTurnoverRecorder recorder,
            IRuntimeActorStateSchemaContextReader schemaContext)
        {
            _recorder = recorder;
            _schemaContext = schemaContext;
            _recorder.RecordConstruction(_schemaContext.Current);
        }

        public string Id => PhaseBTurnoverAgentKind;

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _recorder.RecordActivation(_schemaContext.Current);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _recorder.RecordDeactivation(_schemaContext.Current);
            return Task.CompletedTask;
        }

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = _recorder.RecordHandlerAttempt(_schemaContext.Current, envelope);
            if (attempt == 1)
            {
                await _recorder.WaitUntilFirstRejectionAllowedAsync();
                var disposition = System.Enum.Parse<ProjectionWriteDisposition>(
                    envelope.Payload!.Unpack<StringValue>().Value,
                    ignoreCase: false);
                throw new ProjectionScopeStatusWriteRejectedException(
                    StreamForwardingEnvelopeState.GetTargetStreamId(envelope) ?? string.Empty,
                    new ProjectionSourceCoordinate
                    {
                        ActorId = StreamForwardingEnvelopeState.GetSourceStreamId(envelope),
                        StateVersion = 17,
                        EventId = envelope.Id,
                    },
                    disposition);
            }

            await _recorder.WaitUntilFinalSuccessAllowedAsync();
            _recorder.RecordSuccess(envelope);
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }

    [ActorStateMigration(
        PhaseBTurnoverAgentKind,
        RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
        RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
        RequiredContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion)]
    public sealed class PhaseBTurnoverStateV0ToV1Migration : IActorStateMigration<EventEnvelope>
    {
        public int FromStateVersion => 0;

        public int ToStateVersion => 1;

        public EventEnvelope Apply(EventEnvelope state)
        {
            ArgumentNullException.ThrowIfNull(state);
            return state.Clone();
        }
    }

    [GAgent("tests.always-succeed-on-next")]
    public sealed class AlwaysSucceedOnNextAgent : IAgent
    {
        public string Id => "always-succeed-on-next-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(
                    Environment.GetEnvironmentVariable(CrashWorkerEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal) &&
                string.Equals(
                    envelope.Id,
                    Environment.GetEnvironmentVariable(CrashWorkerEnvelopeIdEnvironmentVariable),
                    StringComparison.Ordinal))
            {
                File.WriteAllText(
                    RequireEnvironmentVariable(CrashWorkerHandlerMarkerEnvironmentVariable),
                    envelope.Id);
            }

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

public sealed class KafkaAckCrashWorkerFactAttribute : FactAttribute
{
    public KafkaAckCrashWorkerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AEVATAR_KAFKA_ACK_CRASH_WORKER"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Executed only by the isolated Kafka pre-ACK crash-window harness.";
        }
    }
}
