using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Serialization;
using PbValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Integration.Tests;

public sealed class ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests
{
    [Trait("Category", "Slow")]
    [Orleans3ClusterIntegrationFact]
    public async Task ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos()
    {
        var clusterId = $"aevatar-script-cluster-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-script-service-{Guid.NewGuid():N}";
        var streamProviderName = $"aevatar-script-provider-{Guid.NewGuid():N}";
        var actorEventNamespace = $"aevatar.script.cluster.{Guid.NewGuid():N}";

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
            primarySiloEndpoint: null);
        var node2 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            streamProviderName,
            actorEventNamespace,
            node2SiloPort,
            node2GatewayPort,
            primarySiloEndpoint: primaryEndpoint);
        var node3 = await StartSiloHostAsync(
            clusterId,
            serviceId,
            streamProviderName,
            actorEventNamespace,
            node3SiloPort,
            node3GatewayPort,
            primarySiloEndpoint: primaryEndpoint);

        try
        {
            var runtimeNode1 = node1.Services.GetRequiredService<IActorRuntime>();
            var runtimeNode2 = node2.Services.GetRequiredService<IActorRuntime>();
            var runtimeNode3 = node3.Services.GetRequiredService<IActorRuntime>();
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
            var workerAV2Source = BuildSimpleRuntimeSource("OrleansWorkerARev2Runtime", "OrleansWorkerARev2CompletedEvent");
            var workerBV2Source = BuildSimpleRuntimeSource("OrleansWorkerBRev2Runtime", "OrleansWorkerBRev2CompletedEvent");
            var generatedSource = BuildSimpleRuntimeSource("OrleansGeneratedRuntime", "OrleansGeneratedCompletedEvent");
            var convergenceProbeActorId = $"orleans-convergence-probe-{scopeId}";

            _ = await runtimeNode1.CreateAsync<ScriptCatalogGAgent>(convergenceProbeActorId);
            (await EventuallyAsync(() => runtimeNode2.ExistsAsync(convergenceProbeActorId))).Should().BeTrue();
            (await EventuallyAsync(() => runtimeNode3.ExistsAsync(convergenceProbeActorId))).Should().BeTrue();

            await UpsertDefinitionAsync(
                runtimeNode1,
                workerADefinitionActorId,
                workerAScriptId,
                "rev-a-1",
                BuildSimpleRuntimeSource("OrleansWorkerARev1Runtime", "OrleansWorkerARev1CompletedEvent"));
            await UpsertDefinitionAsync(
                runtimeNode1,
                workerBDefinitionActorId,
                workerBScriptId,
                "rev-b-1",
                BuildSimpleRuntimeSource("OrleansWorkerBRev1Runtime", "OrleansWorkerBRev1CompletedEvent"));
            await UpsertDefinitionAsync(
                runtimeNode1,
                orchestratorDefinitionActorId,
                $"orleans-orchestrator-script-{scopeId}",
                "rev-orchestrator-1",
                OrleansClusterOrchestratorSource);

            (await EventuallyAsync(() =>
                DefinitionSnapshotExistsAsync(
                    definitionSnapshotPortNode2,
                    workerADefinitionActorId,
                    "rev-a-1"))).Should().BeTrue();
            (await EventuallyAsync(() =>
                DefinitionSnapshotExistsAsync(
                    definitionSnapshotPortNode3,
                    workerBDefinitionActorId,
                    "rev-b-1"))).Should().BeTrue();
            (await EventuallyAsync(() =>
                DefinitionSnapshotExistsAsync(
                    definitionSnapshotPortNode2,
                    orchestratorDefinitionActorId,
                    "rev-orchestrator-1"))).Should().BeTrue();

            var orchestratorRuntime = await runtimeNode1.CreateAsync<ScriptRuntimeGAgent>(orchestratorRuntimeActorId);
            await RunScriptAsync(
                orchestratorRuntime,
                orchestratorRuntimeActorId,
                new RunScriptActorRequest(
                    RunId: $"run-orleans-{scopeId}",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields =
                        {
                            ["worker_a_definition_actor_id"] = PbValue.ForString(workerADefinitionActorId),
                            ["worker_b_definition_actor_id"] = PbValue.ForString(workerBDefinitionActorId),
                            ["worker_a_script_id"] = PbValue.ForString(workerAScriptId),
                            ["worker_b_script_id"] = PbValue.ForString(workerBScriptId),
                            ["new_script_id"] = PbValue.ForString(newScriptId),
                            ["worker_a_v2_source"] = PbValue.ForString(workerAV2Source),
                            ["worker_b_v2_source"] = PbValue.ForString(workerBV2Source),
                            ["new_script_source"] = PbValue.ForString(generatedSource),
                            ["temp_a_runtime_id"] = PbValue.ForString(tempARuntimeId),
                            ["temp_b_runtime_id"] = PbValue.ForString(tempBRuntimeId),
                            ["generated_runtime_id"] = PbValue.ForString(generatedRuntimeId),
                            ["generated_definition_actor_id"] = PbValue.ForString(generatedDefinitionActorId),
                        },
                    }),
                    ScriptRevision: "rev-orchestrator-1",
                    DefinitionActorId: orchestratorDefinitionActorId,
                    RequestedEventType: "script.orleans.cluster.orchestrate"));

            _ = tempARuntimeId;
            _ = tempBRuntimeId;
            _ = generatedRuntimeId;
            _ = generatedDefinitionActorId;
            _ = orchestratorRuntimeActorId;

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
                var entry = await catalogQueryPortNode1.GetCatalogEntryAsync("script-catalog", workerAScriptId, CancellationToken.None);
                return entry != null && string.Equals(entry.ActiveRevision, "rev-a-2", StringComparison.Ordinal);
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                var entry = await catalogQueryPortNode1.GetCatalogEntryAsync("script-catalog", workerBScriptId, CancellationToken.None);
                return entry != null && string.Equals(entry.ActiveRevision, "rev-b-2", StringComparison.Ordinal);
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                workerACatalogEntry = await catalogQueryPortNode3.GetCatalogEntryAsync("script-catalog", workerAScriptId, CancellationToken.None);
                return workerACatalogEntry != null && string.Equals(workerACatalogEntry.ActiveRevision, "rev-a-2", StringComparison.Ordinal);
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                workerBCatalogEntry = await catalogQueryPortNode3.GetCatalogEntryAsync("script-catalog", workerBScriptId, CancellationToken.None);
                return workerBCatalogEntry != null && string.Equals(workerBCatalogEntry.ActiveRevision, "rev-b-2", StringComparison.Ordinal);
            })).Should().BeTrue();

            workerACatalogEntry.Should().NotBeNull();
            workerBCatalogEntry.Should().NotBeNull();
            workerACatalogEntry!.ActiveRevision.Should().Be("rev-a-2");
            workerBCatalogEntry!.ActiveRevision.Should().Be("rev-b-2");
            workerACatalogEntry.ActiveDefinitionActorId.Should().Be(decisionA.DefinitionActorId);
            workerBCatalogEntry.ActiveDefinitionActorId.Should().Be(decisionB.DefinitionActorId);
            workerACatalogEntry.RevisionHistory.Should().Contain("rev-a-2");
            workerBCatalogEntry.RevisionHistory.Should().Contain("rev-b-2");

            ScriptDefinitionSnapshot? workerADefinitionSnapshot = null;
            ScriptDefinitionSnapshot? workerBDefinitionSnapshot = null;
            ScriptDefinitionSnapshot? generatedDefinitionSnapshot = null;
            (await EventuallyAsync(async () =>
            {
                try
                {
                    workerADefinitionSnapshot = await definitionSnapshotPortNode2.GetRequiredAsync(
                        decisionA.DefinitionActorId,
                        "rev-a-2",
                        CancellationToken.None);
                    return true;
                }
                catch
                {
                    return false;
                }
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                try
                {
                    workerBDefinitionSnapshot = await definitionSnapshotPortNode3.GetRequiredAsync(
                        decisionB.DefinitionActorId,
                        "rev-b-2",
                        CancellationToken.None);
                    return true;
                }
                catch
                {
                    return false;
                }
            })).Should().BeTrue();
            (await EventuallyAsync(async () =>
            {
                try
                {
                    generatedDefinitionSnapshot = await definitionSnapshotPortNode3.GetRequiredAsync(
                        generatedDefinitionActorId,
                        "rev-new-1",
                        CancellationToken.None);
                    return true;
                }
                catch
                {
                    return false;
                }
            })).Should().BeTrue();

            workerADefinitionSnapshot!.ScriptId.Should().Be(workerAScriptId);
            workerADefinitionSnapshot.Revision.Should().Be("rev-a-2");
            workerBDefinitionSnapshot!.ScriptId.Should().Be(workerBScriptId);
            workerBDefinitionSnapshot.Revision.Should().Be("rev-b-2");
            generatedDefinitionSnapshot!.ScriptId.Should().Be(newScriptId);
            generatedDefinitionSnapshot.Revision.Should().Be("rev-new-1");
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
        IPEndPoint? primarySiloEndpoint)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    primarySiloEndpoint: primarySiloEndpoint,
                    serviceId: serviceId,
                    clusterId: clusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.StreamProviderName = streamProviderName;
                    options.ActorEventNamespace = actorEventNamespace;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                    services.AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer()));
            })
            .ConfigureServices(services =>
            {
                services.AddScriptCapability();
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

    private static async Task UpsertDefinitionAsync(
        IActorRuntime runtime,
        string definitionActorId,
        string scriptId,
        string revision,
        string source)
    {
        var actor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await actor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: scriptId,
                    ScriptRevision: revision,
                    SourceText: source,
                    SourceHash: $"hash-{scriptId}-{revision}"),
                definitionActorId),
            CancellationToken.None);
    }

    private static async Task RunScriptAsync(
        IActor runtimeActor,
        string runtimeActorId,
        RunScriptActorRequest command)
    {
        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(run.Map(command, runtimeActorId), CancellationToken.None);
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

    private static string BuildSimpleRuntimeSource(string className, string completedEventType) =>
        $$"""
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class {{className}} : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = requestedEvent;
        _ = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "{{completedEventType}}" },
            }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";

    private const string OrleansClusterOrchestratorSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class OrleansClusterScriptOrchestrator : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var workerADefinitionActorId = input.Fields["worker_a_definition_actor_id"].StringValue;
        var workerBDefinitionActorId = input.Fields["worker_b_definition_actor_id"].StringValue;
        var workerAScriptId = input.Fields["worker_a_script_id"].StringValue;
        var workerBScriptId = input.Fields["worker_b_script_id"].StringValue;
        var newScriptId = input.Fields["new_script_id"].StringValue;
        var workerAV2Source = input.Fields["worker_a_v2_source"].StringValue;
        var workerBV2Source = input.Fields["worker_b_v2_source"].StringValue;
        var newScriptSource = input.Fields["new_script_source"].StringValue;
        var tempARuntimeId = input.Fields["temp_a_runtime_id"].StringValue;
        var tempBRuntimeId = input.Fields["temp_b_runtime_id"].StringValue;
        var generatedRuntimeId = input.Fields["generated_runtime_id"].StringValue;
        var generatedDefinitionActorId = input.Fields["generated_definition_actor_id"].StringValue;

        tempARuntimeId = await context.Capabilities!.SpawnScriptRuntimeAsync(
            workerADefinitionActorId,
            "rev-a-1",
            tempARuntimeId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            tempARuntimeId,
            "temp-a-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-a-1",
            workerADefinitionActorId,
            "temp.a.run",
            ct);

        tempBRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            workerBDefinitionActorId,
            "rev-b-1",
            tempBRuntimeId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            tempBRuntimeId,
            "temp-b-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-b-1",
            workerBDefinitionActorId,
            "temp.b.run",
            ct);

        generatedDefinitionActorId = await context.Capabilities.UpsertScriptDefinitionAsync(
            newScriptId,
            "rev-new-1",
            newScriptSource,
            "hash-new-1",
            generatedDefinitionActorId,
            ct);
        generatedRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            generatedDefinitionActorId,
            "rev-new-1",
            generatedRuntimeId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            generatedRuntimeId,
            "generated-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-new-1",
            generatedDefinitionActorId,
            "generated.run",
            ct);

        var summary = new Struct
        {
            Fields =
            {
                ["temp_a_runtime_id"] = Value.ForString(tempARuntimeId),
                ["temp_b_runtime_id"] = Value.ForString(tempBRuntimeId),
                ["generated_runtime_id"] = Value.ForString(generatedRuntimeId),
                ["generated_definition_actor_id"] = Value.ForString(generatedDefinitionActorId),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "OrleansClusterOrchestrationCompletedEvent" },
            },
            new Dictionary<string, Any>
            {
                ["summary"] = Any.Pack(summary),
            },
            new Dictionary<string, Any>
            {
                ["summary"] = Any.Pack(summary),
            });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";
}
