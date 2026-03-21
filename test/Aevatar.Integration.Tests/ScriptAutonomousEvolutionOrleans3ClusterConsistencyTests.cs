using System.Net;
using System.Net.Sockets;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Serialization;

namespace Aevatar.Integration.Tests;

public sealed class ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests
{
    [Trait("Category", "Slow")]
    [Orleans3ClusterIntegrationFact]
    public async Task ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos()
    {
        var kafkaBootstrapServers = RequireKafkaBootstrapServers();
        var garnetConnectionString = RequireGarnetConnectionString();
        var clusterId = $"aevatar-script-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-script-service-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-script-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.script.cluster.{Guid.NewGuid():N}";
        var kafkaTopicName = $"aevatar-script-cluster-topic-{Guid.NewGuid():N}";
        var consumerGroup = $"aevatar-script-cluster-consumer-{Guid.NewGuid():N}";
        var projectionIndexPrefix = $"aevatar-script-cluster-{Guid.NewGuid():N}";

        var node1SiloPort = ReserveTcpPort();
        var node1GatewayPort = ReserveTcpPort();
        var node2SiloPort = ReserveTcpPort();
        var node2GatewayPort = ReserveTcpPort();
        var node3SiloPort = ReserveTcpPort();
        var node3GatewayPort = ReserveTcpPort();
        var primaryEndpoint = new IPEndPoint(IPAddress.Loopback, node1SiloPort);

        var node1 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            streamProviderName,
            actorEventNamespace,
            node1SiloPort,
            node1GatewayPort,
            null,
            garnetConnectionString,
            kafkaBootstrapServers,
            kafkaTopicName,
            consumerGroup,
            projectionIndexPrefix);
        var node2 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            streamProviderName,
            actorEventNamespace,
            node2SiloPort,
            node2GatewayPort,
            primaryEndpoint,
            garnetConnectionString,
            kafkaBootstrapServers,
            kafkaTopicName,
            consumerGroup,
            projectionIndexPrefix);
        var node3 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            streamProviderName,
            actorEventNamespace,
            node3SiloPort,
            node3GatewayPort,
            primaryEndpoint,
            garnetConnectionString,
            kafkaBootstrapServers,
            kafkaTopicName,
            consumerGroup,
            projectionIndexPrefix);

        try
        {
            var definitionPortNode1 = node1.Services.GetRequiredService<IScriptDefinitionCommandPort>();
            var provisioningPortNode1 = node1.Services.GetRequiredService<IScriptRuntimeProvisioningPort>();
            var runtimeNode1 = node1.Services.GetRequiredService<IActorRuntime>();
            var executionProjectionNode1 = node1.Services.GetRequiredService<IScriptExecutionProjectionPort>();
            var evolutionServiceNode2 = node2.Services.GetRequiredService<IScriptEvolutionApplicationService>();
            var evolutionServiceNode3 = node3.Services.GetRequiredService<IScriptEvolutionApplicationService>();
            var catalogQueryPortNode1 = node1.Services.GetRequiredService<IScriptCatalogQueryPort>();
            var catalogQueryPortNode3 = node3.Services.GetRequiredService<IScriptCatalogQueryPort>();
            var definitionSnapshotPortNode2 = node2.Services.GetRequiredService<IScriptDefinitionSnapshotPort>();
            var definitionSnapshotPortNode3 = node3.Services.GetRequiredService<IScriptDefinitionSnapshotPort>();

            var scopeId = Guid.NewGuid().ToString("N")[..10];
            var workerADefinitionActorId = $"orleans-worker-a-definition-{scopeId}";
            var workerBDefinitionActorId = $"orleans-worker-b-definition-{scopeId}";
            var orchestratorDefinitionActorId = $"orleans-orchestrator-definition-{scopeId}";
            var orchestratorRuntimeActorId = $"orleans-orchestrator-runtime-{scopeId}";
            var workerAScriptId = $"orleans-worker-a-script-{scopeId}";
            var workerBScriptId = $"orleans-worker-b-script-{scopeId}";
            var newScriptId = $"orleans-generated-script-{scopeId}";
            var tempARuntimeId = $"temp-a-{scopeId}";
            var tempBRuntimeId = $"temp-b-{scopeId}";
            var generatedRuntimeId = $"generated-runtime-{scopeId}";
            var generatedDefinitionActorId = $"generated-definition-{scopeId}";
            var workerAV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "OrleansWorkerARev2Runtime",
                "ORLEANS-A-V2",
                "orleans_worker_a",
                "2");
            var workerBV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "OrleansWorkerBRev2Runtime",
                "ORLEANS-B-V2",
                "orleans_worker_b",
                "2");
            var generatedSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "OrleansGeneratedRuntime",
                "ORLEANS-GENERATED",
                "orleans_generated",
                "1");

            await definitionPortNode1.UpsertDefinitionAsync(
                workerAScriptId,
                "rev-a-1",
                ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "OrleansWorkerARev1Runtime",
                    "ORLEANS-A-V1",
                    "orleans_worker_a",
                    "1"),
                ScriptingCommandEnvelopeTestKit.ComputeSourceHash(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "OrleansWorkerARev1Runtime",
                    "ORLEANS-A-V1",
                    "orleans_worker_a",
                    "1")),
                workerADefinitionActorId,
                CancellationToken.None);
            await definitionPortNode1.UpsertDefinitionAsync(
                workerBScriptId,
                "rev-b-1",
                ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "OrleansWorkerBRev1Runtime",
                    "ORLEANS-B-V1",
                    "orleans_worker_b",
                    "1"),
                ScriptingCommandEnvelopeTestKit.ComputeSourceHash(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "OrleansWorkerBRev1Runtime",
                    "ORLEANS-B-V1",
                    "orleans_worker_b",
                    "1")),
                workerBDefinitionActorId,
                CancellationToken.None);
            var orchestratorDefinition = await definitionPortNode1.UpsertDefinitionWithSnapshotAsync(
                $"orleans-orchestrator-script-{scopeId}",
                "rev-orchestrator-1",
                ScriptEvolutionIntegrationSources.OrleansClusterOrchestratorSource,
                ScriptingCommandEnvelopeTestKit.ComputeSourceHash(ScriptEvolutionIntegrationSources.OrleansClusterOrchestratorSource),
                orchestratorDefinitionActorId,
                CancellationToken.None);

            (await EventuallyAsync(() => DefinitionSnapshotExistsAsync(
                definitionSnapshotPortNode2,
                workerADefinitionActorId,
                "rev-a-1"))).Should().BeTrue();
            (await EventuallyAsync(() => DefinitionSnapshotExistsAsync(
                definitionSnapshotPortNode3,
                workerBDefinitionActorId,
                "rev-b-1"))).Should().BeTrue();
            (await EventuallyAsync(() => DefinitionSnapshotExistsAsync(
                definitionSnapshotPortNode2,
                orchestratorDefinitionActorId,
                "rev-orchestrator-1"))).Should().BeTrue();

            await provisioningPortNode1.EnsureRuntimeAsync(
                orchestratorDefinitionActorId,
                "rev-orchestrator-1",
                orchestratorRuntimeActorId,
                orchestratorDefinition.Snapshot,
                CancellationToken.None);

            var lease = await executionProjectionNode1.EnsureActorProjectionAsync(
                orchestratorRuntimeActorId,
                CancellationToken.None);
            lease.Should().NotBeNull();
            await using var sink = new EventChannel<EventEnvelope>(capacity: 32);
            await executionProjectionNode1.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);

            try
            {
                var orchestratorRuntime = await runtimeNode1.GetAsync(orchestratorRuntimeActorId);
                orchestratorRuntime.Should().NotBeNull();
                await orchestratorRuntime!.HandleEventAsync(
                    ScriptingActorRequestEnvelopeFactory.Create(
                        orchestratorRuntimeActorId,
                        $"run-orleans-{scopeId}",
                        new RunScriptRequestedEvent
                        {
                            RunId = $"run-orleans-{scopeId}",
                            InputPayload = Any.Pack(new OrleansClusterRequested
                            {
                                WorkerADefinitionActorId = workerADefinitionActorId,
                                WorkerBDefinitionActorId = workerBDefinitionActorId,
                                NewScriptId = newScriptId,
                                NewScriptSource = generatedSource,
                                TempARuntimeId = tempARuntimeId,
                                TempBRuntimeId = tempBRuntimeId,
                                GeneratedRuntimeId = generatedRuntimeId,
                                GeneratedDefinitionActorId = generatedDefinitionActorId,
                            }),
                            ScriptRevision = "rev-orchestrator-1",
                            DefinitionActorId = orchestratorDefinitionActorId,
                            RequestedEventType = "script.orleans.cluster.orchestrate",
                            CommandId = $"run-orleans-{scopeId}",
                            CorrelationId = $"run-orleans-{scopeId}",
                        }),
                    CancellationToken.None);

                var committed = await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(
                    sink,
                    $"run-orleans-{scopeId}",
                    CancellationToken.None,
                    TimeSpan.FromSeconds(45));
                committed.DomainEventPayload.Should().NotBeNull();
                var summary = committed.DomainEventPayload!.Unpack<OrleansClusterCompleted>().Current;
                summary.GeneratedDefinitionActorId.Should().Be(generatedDefinitionActorId);
                summary.GeneratedRuntimeId.Should().Be(generatedRuntimeId);
            }
            finally
            {
                await executionProjectionNode1.DetachLiveSinkAsync(lease!, sink, CancellationToken.None);
                await executionProjectionNode1.ReleaseActorProjectionAsync(lease!, CancellationToken.None);
            }

            (await EventuallyAsync(async () =>
            {
                try
                {
                    var snapshot = await definitionSnapshotPortNode3.GetRequiredAsync(
                        generatedDefinitionActorId,
                        "rev-new-1",
                        CancellationToken.None);
                    return string.Equals(snapshot.ScriptId, newScriptId, StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            })).Should().BeTrue();

            var decisionA = await evolutionServiceNode2.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: workerAScriptId,
                    BaseRevision: "rev-a-1",
                    CandidateRevision: "rev-a-2",
                    CandidateSource: workerAV2Source,
                    CandidateSourceHash: string.Empty,
                    Reason: "orleans-3node-worker-a",
                    ProposalId: $"external-proposal-a-{scopeId}"),
                CancellationToken.None);
            var decisionB = await evolutionServiceNode3.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: workerBScriptId,
                    BaseRevision: "rev-b-1",
                    CandidateRevision: "rev-b-2",
                    CandidateSource: workerBV2Source,
                    CandidateSourceHash: string.Empty,
                    Reason: "orleans-3node-worker-b",
                    ProposalId: $"external-proposal-b-{scopeId}"),
                CancellationToken.None);

            decisionA.Accepted.Should().BeTrue();
            decisionB.Accepted.Should().BeTrue();
            decisionA.Status.Should().Be("promoted");
            decisionB.Status.Should().Be("promoted");
            decisionA.DefinitionActorId.Should().Be($"script-definition:{workerAScriptId}");
            decisionB.DefinitionActorId.Should().Be($"script-definition:{workerBScriptId}");

            ScriptCatalogEntrySnapshot? workerACatalogEntry = null;
            ScriptCatalogEntrySnapshot? workerBCatalogEntry = null;
            (await EventuallyAsync(async () =>
            {
                workerACatalogEntry = await catalogQueryPortNode1.GetCatalogEntryAsync(null, workerAScriptId, CancellationToken.None);
                return workerACatalogEntry != null &&
                       string.Equals(workerACatalogEntry.ActiveRevision, "rev-a-2", StringComparison.Ordinal);
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                workerBCatalogEntry = await catalogQueryPortNode3.GetCatalogEntryAsync(null, workerBScriptId, CancellationToken.None);
                return workerBCatalogEntry != null &&
                       string.Equals(workerBCatalogEntry.ActiveRevision, "rev-b-2", StringComparison.Ordinal);
            })).Should().BeTrue();

            workerACatalogEntry.Should().NotBeNull();
            workerBCatalogEntry.Should().NotBeNull();
            workerACatalogEntry!.ActiveDefinitionActorId.Should().Be(decisionA.DefinitionActorId);
            workerBCatalogEntry!.ActiveDefinitionActorId.Should().Be(decisionB.DefinitionActorId);
            workerACatalogEntry.RevisionHistory.Should().Contain("rev-a-2");
            workerBCatalogEntry.RevisionHistory.Should().Contain("rev-b-2");

            (await EventuallyAsync(() => DefinitionSnapshotExistsAsync(
                definitionSnapshotPortNode2,
                decisionA.DefinitionActorId,
                "rev-a-2"))).Should().BeTrue();
            (await EventuallyAsync(() => DefinitionSnapshotExistsAsync(
                definitionSnapshotPortNode3,
                decisionB.DefinitionActorId,
                "rev-b-2"))).Should().BeTrue();
        }
        finally
        {
            await StopHostAsync(node3);
            await StopHostAsync(node2);
            await StopHostAsync(node1);
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        string clusterId,
        string serviceId,
        string streamProviderName,
        string actorEventNamespace,
        int siloPort,
        int gatewayPort,
        IPEndPoint? primarySiloEndpoint,
        string garnetConnectionString,
        string kafkaBootstrapServers,
        string kafkaTopicName,
        string kafkaConsumerGroup,
        string projectionIndexPrefix)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(configurationBuilder =>
                configurationBuilder.AddInMemoryCollection(
                    BuildProjectionConfigurationValues(projectionIndexPrefix)))
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort,
                    gatewayPort,
                    primarySiloEndpoint,
                    serviceId,
                    clusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = garnetConnectionString;
                    options.QueueCount = 4;
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport();
                siloBuilder.ConfigureServices(services =>
                    services.AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer()));
            })
            .ConfigureServices((context, services) =>
            {
                services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
                {
                    options.BootstrapServers = kafkaBootstrapServers;
                    options.TopicName = kafkaTopicName;
                    options.ConsumerGroup = kafkaConsumerGroup;
                    options.TopicPartitionCount = 4;
                });
                services.AddScriptCapability(context.Configuration);
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task StopHostAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> EventuallyAsync(
        Func<Task<bool>> probe,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var waitTimeout = timeout ?? TimeSpan.FromSeconds(45);
        var waitInterval = interval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow + waitTimeout;

        while (DateTime.UtcNow <= deadline)
        {
            if (await probe())
                return true;

            await Task.Delay(waitInterval);
        }

        return false;
    }

    private static async Task<bool> DefinitionSnapshotExistsAsync(
        IScriptDefinitionSnapshotPort snapshotPort,
        string definitionActorId,
        string revision)
    {
        try
        {
            _ = await snapshotPort.GetRequiredAsync(definitionActorId, revision, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    private static string RequireKafkaBootstrapServers() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS.");

    private static Dictionary<string, string?> BuildProjectionConfigurationValues(string indexPrefix)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = bool.TrueString,
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = RequireEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT"),
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = indexPrefix,
            ["Projection:Document:Providers:Elasticsearch:AutoCreateIndex"] = bool.TrueString,
            ["Projection:Document:Providers:InMemory:Enabled"] = bool.FalseString,
            ["Projection:Graph:Providers:Neo4j:Enabled"] = bool.TrueString,
            ["Projection:Graph:Providers:Neo4j:Uri"] = RequireEnvironmentVariable("AEVATAR_TEST_NEO4J_URI"),
            ["Projection:Graph:Providers:Neo4j:Username"] = RequireEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME"),
            ["Projection:Graph:Providers:Neo4j:Password"] = RequireEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD"),
            ["Projection:Graph:Providers:InMemory:Enabled"] = bool.FalseString,
            ["Projection:Policies:Environment"] = "Production",
            ["Projection:Policies:DenyInMemoryDocumentReadStore"] = bool.TrueString,
            ["Projection:Policies:DenyInMemoryGraphFactStore"] = bool.TrueString,
        };
    }

    private static string RequireEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing {name}.");
}
