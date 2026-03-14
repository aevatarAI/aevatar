using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using PbValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Slow")]
public class ScriptAutonomousEvolutionComprehensiveE2ETests
{
    [Fact]
    public async Task ScriptOnlyFlow_ShouldDriveMultiScriptMultiRoundEvolution_AndDynamicLifecycle()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string workerADefinitionActorId = "multi-worker-a-definition";
        const string workerBDefinitionActorId = "multi-worker-b-definition";
        const string orchestratorDefinitionActorId = "multi-orchestrator-definition";
        const string orchestratorRuntimeActorId = "multi-orchestrator-runtime";

        await UpsertDefinitionAsync(
            runtime,
            workerADefinitionActorId,
            scriptId: "worker-a-script",
            revision: "rev-a-1",
            source: BuildSimpleRuntimeSource("WorkerARev1Runtime", "WorkerARev1CompletedEvent"));
        await UpsertDefinitionAsync(
            runtime,
            workerBDefinitionActorId,
            scriptId: "worker-b-script",
            revision: "rev-b-1",
            source: BuildSimpleRuntimeSource("WorkerBRev1Runtime", "WorkerBRev1CompletedEvent"));
        await UpsertDefinitionAsync(
            runtime,
            orchestratorDefinitionActorId,
            scriptId: "multi-orchestrator-script",
            revision: "rev-orchestrator-1",
            source: MultiScriptOrchestratorSource);

        var orchestratorRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(orchestratorRuntimeActorId);
        await RunScriptAsync(
            orchestratorRuntime,
            orchestratorRuntimeActorId,
            new RunScriptRequestedEvent
            {
                RunId = "run-multi-script-1",
                InputPayload = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["worker_a_v2_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("WorkerARev2Runtime", "WorkerARev2CompletedEvent")),
                        ["worker_a_v3_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("WorkerARev3Runtime", "WorkerARev3CompletedEvent")),
                        ["worker_b_v2_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("WorkerBRev2Runtime", "WorkerBRev2CompletedEvent")),
                        ["worker_b_v3_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("WorkerBRev3Runtime", "WorkerBRev3CompletedEvent")),
                        ["generated_source_1"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("GeneratedRound1Runtime", "GeneratedRound1CompletedEvent")),
                        ["generated_source_2"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("GeneratedRound2Runtime", "GeneratedRound2CompletedEvent")),
                        ["runtime_agent_type"] = PbValue.ForString(
                            typeof(ScriptRuntimeGAgent).AssemblyQualifiedName
                            ?? throw new InvalidOperationException("ScriptRuntimeGAgent type name is required.")),
                    },
                }),
                ScriptRevision = "rev-orchestrator-1",
                DefinitionActorId = orchestratorDefinitionActorId,
                RequestedEventType = "script.autonomous.multi.orchestrate",
            });

        var summary = GetSummary(orchestratorRuntime);
        summary.Fields["decision_a2"].StringValue.Should().Be("promoted");
        summary.Fields["decision_a3"].StringValue.Should().Be("promoted");
        summary.Fields["decision_b2"].StringValue.Should().Be("promoted");
        summary.Fields["decision_b3"].StringValue.Should().Be("promoted");

        var lifecycleActorId = summary.Fields["lifecycle_actor_id"].StringValue;
        var tempARuntimeId = summary.Fields["temp_a_runtime_id"].StringValue;
        var tempBRuntimeId = summary.Fields["temp_b_runtime_id"].StringValue;
        var generatedRuntimeId1 = summary.Fields["generated_runtime_id_1"].StringValue;
        var generatedRuntimeId2 = summary.Fields["generated_runtime_id_2"].StringValue;
        var evolvedARuntimeId = summary.Fields["evolved_a_runtime_id"].StringValue;
        var evolvedBRuntimeId = summary.Fields["evolved_b_runtime_id"].StringValue;

        (await runtime.ExistsAsync(lifecycleActorId)).Should().BeFalse();
        (await runtime.ExistsAsync(tempARuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(tempBRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId1)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId2)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedARuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedBRuntimeId)).Should().BeTrue();

        var manager = (ScriptEvolutionManagerGAgent)(await runtime.GetAsync("script-evolution-manager"))!.Agent;
        manager.State.Proposals.Should().ContainKey("proposal-a-2-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-a-3-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-b-2-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-b-3-run-multi-script-1");
        manager.State.Proposals["proposal-a-2-run-multi-script-1"].Status.Should().Be("promoted");
        manager.State.Proposals["proposal-a-3-run-multi-script-1"].Status.Should().Be("promoted");
        manager.State.Proposals["proposal-b-2-run-multi-script-1"].Status.Should().Be("promoted");
        manager.State.Proposals["proposal-b-3-run-multi-script-1"].Status.Should().Be("promoted");

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("worker-a-script");
        catalog.State.Entries.Should().ContainKey("worker-b-script");
        catalog.State.Entries["worker-a-script"].ActiveRevision.Should().Be("rev-a-3");
        catalog.State.Entries["worker-b-script"].ActiveRevision.Should().Be("rev-b-3");
        catalog.State.Entries["worker-a-script"].ActiveDefinitionActorId.Should().Be("script-definition:worker-a-script");
        catalog.State.Entries["worker-b-script"].ActiveDefinitionActorId.Should().Be("script-definition:worker-b-script");
        catalog.State.Entries["worker-a-script"].RevisionHistory.Should().Contain("rev-a-2");
        catalog.State.Entries["worker-a-script"].RevisionHistory.Should().Contain("rev-a-3");
        catalog.State.Entries["worker-b-script"].RevisionHistory.Should().Contain("rev-b-2");
        catalog.State.Entries["worker-b-script"].RevisionHistory.Should().Contain("rev-b-3");

        var workerADefinition = (ScriptDefinitionGAgent)(await runtime.GetAsync(
            catalog.State.Entries["worker-a-script"].ActiveDefinitionActorId))!.Agent;
        var workerBDefinition = (ScriptDefinitionGAgent)(await runtime.GetAsync(
            catalog.State.Entries["worker-b-script"].ActiveDefinitionActorId))!.Agent;
        workerADefinition.State.Revision.Should().Be("rev-a-3");
        workerBDefinition.State.Revision.Should().Be("rev-b-3");

        var orchestratorEvents = await eventStore.GetEventsAsync(orchestratorRuntimeActorId, ct: CancellationToken.None);
        orchestratorEvents.Should().Contain(x =>
            x.EventData != null && x.EventData.Is(ScriptRunDomainEventCommitted.Descriptor));
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldSelfEvolveAcrossGenerations_WithoutFrameworkChanges()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string definitionActorId = "self-evolving-definition";
        const string runtimeActorId = "self-evolving-runtime";

        await UpsertDefinitionAsync(
            runtime,
            definitionActorId,
            scriptId: "self-evolving-script",
            revision: "rev-self-1",
            source: SelfEvolutionV1Source);

        var rootRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
        await RunScriptAsync(
            rootRuntime,
            runtimeActorId,
            new RunScriptRequestedEvent
            {
                RunId = "run-self-1",
                InputPayload = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["next_v2_source"] = PbValue.ForString(SelfEvolutionV2Source),
                        ["next_v3_source"] = PbValue.ForString(SelfEvolutionV3Source),
                        ["generated_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("SelfGeneratedRuntime", "SelfGeneratedCompletedEvent")),
                    },
                }),
                ScriptRevision = "rev-self-1",
                DefinitionActorId = definitionActorId,
                RequestedEventType = "script.self.evolve",
            });

        var v1Summary = GetSummary(rootRuntime);
        v1Summary.Fields["decision_v2"].StringValue.Should().Be("promoted");
        var v2RuntimeId = v1Summary.Fields["v2_runtime_id"].StringValue;
        var generatedRuntimeId = v1Summary.Fields["generated_runtime_id"].StringValue;
        (await runtime.ExistsAsync(v2RuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId)).Should().BeTrue();

        var v2Runtime = (ScriptRuntimeGAgent)(await runtime.GetAsync(v2RuntimeId))!.Agent;
        var v2Summary = v2Runtime.State.StatePayloads["summary"].Unpack<Struct>();
        v2Summary.Fields["decision_v3"].StringValue.Should().Be("promoted");
        var v3RuntimeId = v2Summary.Fields["v3_runtime_id"].StringValue;
        (await runtime.ExistsAsync(v3RuntimeId)).Should().BeTrue();

        var manager = (ScriptEvolutionManagerGAgent)(await runtime.GetAsync("script-evolution-manager"))!.Agent;
        manager.State.Proposals.Should().ContainKey("self-proposal-v2-run-self-1");
        manager.State.Proposals.Should().ContainKey("self-proposal-v3-self-v2-run-run-self-1");
        manager.State.Proposals["self-proposal-v2-run-self-1"].Status.Should().Be("promoted");
        manager.State.Proposals["self-proposal-v3-self-v2-run-run-self-1"].Status.Should().Be("promoted");

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("self-evolving-script");
        catalog.State.Entries["self-evolving-script"].ActiveRevision.Should().Be("rev-self-3");
        catalog.State.Entries["self-evolving-script"].ActiveDefinitionActorId.Should().Be("script-definition:self-evolving-script");
        catalog.State.Entries["self-evolving-script"].RevisionHistory.Should().Contain("rev-self-2");
        catalog.State.Entries["self-evolving-script"].RevisionHistory.Should().Contain("rev-self-3");

        var definition = (ScriptDefinitionGAgent)(await runtime.GetAsync(
            catalog.State.Entries["self-evolving-script"].ActiveDefinitionActorId))!.Agent;
        definition.State.Revision.Should().Be("rev-self-3");

        var v3Events = await eventStore.GetEventsAsync(v3RuntimeId, ct: CancellationToken.None);
        v3Events.Should().Contain(x =>
            x.EventData != null &&
            x.EventData.Is(ScriptRunDomainEventCommitted.Descriptor) &&
            x.EventData.Unpack<ScriptRunDomainEventCommitted>().EventType == "SelfEvolutionV3CompletedEvent");
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldUseDirectCatalogPromoteAndRollback_FromCapabilities()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string controllerDefinitionActorId = "catalog-controller-definition";
        const string controllerRuntimeActorId = "catalog-controller-runtime";

        await UpsertDefinitionAsync(
            runtime,
            controllerDefinitionActorId,
            scriptId: "catalog-controller-script",
            revision: "rev-controller-1",
            source: CatalogControlOrchestratorSource);

        var controllerRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(controllerRuntimeActorId);
        await RunScriptAsync(
            controllerRuntime,
            controllerRuntimeActorId,
            new RunScriptRequestedEvent
            {
                RunId = "run-catalog-control-1",
                InputPayload = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["manual_v1_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("ManualCatalogRev1Runtime", "ManualCatalogRev1CompletedEvent")),
                        ["manual_v2_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("ManualCatalogRev2Runtime", "ManualCatalogRev2CompletedEvent")),
                    },
                }),
                ScriptRevision = "rev-controller-1",
                DefinitionActorId = controllerDefinitionActorId,
                RequestedEventType = "script.catalog.control",
            });

        var summary = GetSummary(controllerRuntime);
        var manualRuntimeId = summary.Fields["manual_runtime_id"].StringValue;
        var manualDefinitionActorIdV1 = summary.Fields["manual_definition_actor_id_v1"].StringValue;
        var manualDefinitionActorIdV2 = summary.Fields["manual_definition_actor_id_v2"].StringValue;

        (await runtime.ExistsAsync(manualRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(manualDefinitionActorIdV1)).Should().BeTrue();
        (await runtime.ExistsAsync(manualDefinitionActorIdV2)).Should().BeTrue();

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("manual-catalog-script");
        catalog.State.Entries["manual-catalog-script"].ActiveRevision.Should().Be("rev-manual-1");
        catalog.State.Entries["manual-catalog-script"].PreviousRevision.Should().Be("rev-manual-2");
        catalog.State.Entries["manual-catalog-script"].RevisionHistory.Should().Contain("rev-manual-1");
        catalog.State.Entries["manual-catalog-script"].RevisionHistory.Should().Contain("rev-manual-2");

        var manualRunEvents = await eventStore.GetEventsAsync(manualRuntimeId, ct: CancellationToken.None);
        manualRunEvents.Should().Contain(x =>
            x.EventData != null &&
            x.EventData.Is(ScriptRunDomainEventCommitted.Descriptor) &&
            x.EventData.Unpack<ScriptRunDomainEventCommitted>().EventType == "ManualCatalogRev2CompletedEvent");
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldExerciseInteractionAndDefinitionUpsertCapabilities()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string controllerDefinitionActorId = "interaction-controller-definition";
        const string controllerRuntimeActorId = "interaction-controller-runtime";

        await UpsertDefinitionAsync(
            runtime,
            controllerDefinitionActorId,
            scriptId: "interaction-controller-script",
            revision: "rev-interaction-1",
            source: InteractionUpsertOrchestratorSource);

        var controllerRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(controllerRuntimeActorId);
        await RunScriptAsync(
            controllerRuntime,
            controllerRuntimeActorId,
            new RunScriptRequestedEvent
            {
                RunId = "run-interaction-1",
                InputPayload = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["definition_agent_type"] = PbValue.ForString(
                            typeof(ScriptDefinitionGAgent).AssemblyQualifiedName
                            ?? throw new InvalidOperationException("ScriptDefinitionGAgent type name is required.")),
                        ["publish_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("PublishedDefinitionRuntime", "PublishedDefinitionEvent")),
                        ["sendto_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("SendToDefinitionRuntime", "SendToDefinitionEvent")),
                        ["invoke_source"] = PbValue.ForString(
                            BuildSimpleRuntimeSource("InvokedDefinitionRuntime", "InvokedDefinitionEvent")),
                    },
                }),
                ScriptRevision = "rev-interaction-1",
                DefinitionActorId = controllerDefinitionActorId,
                RequestedEventType = "script.interaction.exercise",
            });

        var summary = GetSummary(controllerRuntime);
        summary.Fields["ai_response_length"].StringValue.Should().Be("0");

        var publishedDefinitionId = summary.Fields["published_definition_actor_id"].StringValue;
        var sendToDefinitionId = summary.Fields["sendto_definition_actor_id"].StringValue;
        var upsertDefinitionId = summary.Fields["upsert_definition_actor_id"].StringValue;

        (await runtime.ExistsAsync(publishedDefinitionId)).Should().BeTrue();
        (await runtime.ExistsAsync(sendToDefinitionId)).Should().BeTrue();
        (await runtime.ExistsAsync(upsertDefinitionId)).Should().BeTrue();
        var upsertDefinition = (ScriptDefinitionGAgent)(await runtime.GetAsync(upsertDefinitionId))!.Agent;

        upsertDefinition.State.ScriptId.Should().Be("interaction-invoke-script");
        upsertDefinition.State.Revision.Should().Be("rev-invoke-1");
    }

    private static async Task UpsertDefinitionAsync(
        IActorRuntime runtime,
        string definitionActorId,
        string scriptId,
        string revision,
        string source)
    {
        var actor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        await actor.HandleEventAsync(
            ScriptingCommandEnvelopeTestKit.CreateUpsertDefinition(
                definitionActorId,
                scriptId,
                revision,
                source,
                $"hash-{scriptId}-{revision}"),
            CancellationToken.None);
    }

    private static async Task RunScriptAsync(
        IActor runtimeActor,
        string runtimeActorId,
        RunScriptRequestedEvent command)
    {
        await runtimeActor.HandleEventAsync(
            ScriptingCommandEnvelopeTestKit.CreateRunScript(runtimeActorId, command),
            CancellationToken.None);
    }

    private static Struct GetSummary(IActor runtimeActor)
    {
        var runtime = (ScriptRuntimeGAgent)runtimeActor.Agent;
        runtime.State.StatePayloads.Should().ContainKey("summary");
        return runtime.State.StatePayloads["summary"].Unpack<Struct>();
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

    private const string MultiScriptOrchestratorSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class MultiScriptEvolutionOrchestrator : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var workerAV2Source = input.Fields["worker_a_v2_source"].StringValue;
        var workerAV3Source = input.Fields["worker_a_v3_source"].StringValue;
        var workerBV2Source = input.Fields["worker_b_v2_source"].StringValue;
        var workerBV3Source = input.Fields["worker_b_v3_source"].StringValue;
        var generatedSource1 = input.Fields["generated_source_1"].StringValue;
        var generatedSource2 = input.Fields["generated_source_2"].StringValue;
        var runtimeAgentType = input.Fields["runtime_agent_type"].StringValue;

        var lifecycleActorId = await context.Capabilities!.CreateAgentAsync(
            runtimeAgentType,
            "script-created-runtime-" + context.RunId,
            ct);
        await context.Capabilities.LinkAgentsAsync(context.ActorId, lifecycleActorId, ct);
        await context.Capabilities.UnlinkAgentAsync(lifecycleActorId, ct);

        var tempARuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            "multi-worker-a-definition",
            "rev-a-1",
            "temp-worker-a-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            tempARuntimeId,
            "temp-worker-a-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-a-1",
            "multi-worker-a-definition",
            "worker.a.temp",
            ct);

        var tempBRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            "multi-worker-b-definition",
            "rev-b-1",
            "temp-worker-b-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            tempBRuntimeId,
            "temp-worker-b-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-b-1",
            "multi-worker-b-definition",
            "worker.b.temp",
            ct);

        var generatedDefinitionActorId1 = await context.Capabilities.UpsertScriptDefinitionAsync(
            "generated-script-1",
            "rev-g-1",
            generatedSource1,
            "hash-generated-1",
            "generated-definition-1-" + context.RunId,
            ct);
        var generatedRuntimeId1 = await context.Capabilities.SpawnScriptRuntimeAsync(
            generatedDefinitionActorId1,
            "rev-g-1",
            "generated-runtime-1-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            generatedRuntimeId1,
            "generated-run-1-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-g-1",
            generatedDefinitionActorId1,
            "generated.script.1.run",
            ct);

        var decisionA2 = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-a-2-" + context.RunId,
                ScriptId: "worker-a-script",
                BaseRevision: "rev-a-1",
                CandidateRevision: "rev-a-2",
                CandidateSource: workerAV2Source,
                CandidateSourceHash: string.Empty,
                Reason: "upgrade worker-a to rev-a-2"),
            ct);

        var decisionB2 = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-b-2-" + context.RunId,
                ScriptId: "worker-b-script",
                BaseRevision: "rev-b-1",
                CandidateRevision: "rev-b-2",
                CandidateSource: workerBV2Source,
                CandidateSourceHash: string.Empty,
                Reason: "upgrade worker-b to rev-b-2"),
            ct);

        var decisionA3 = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-a-3-" + context.RunId,
                ScriptId: "worker-a-script",
                BaseRevision: "rev-a-2",
                CandidateRevision: "rev-a-3",
                CandidateSource: workerAV3Source,
                CandidateSourceHash: string.Empty,
                Reason: "upgrade worker-a to rev-a-3"),
            ct);

        var decisionB3 = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-b-3-" + context.RunId,
                ScriptId: "worker-b-script",
                BaseRevision: "rev-b-2",
                CandidateRevision: "rev-b-3",
                CandidateSource: workerBV3Source,
                CandidateSourceHash: string.Empty,
                Reason: "upgrade worker-b to rev-b-3"),
            ct);

        var evolvedARuntimeId = string.Empty;
        if (decisionA3.Accepted)
        {
            evolvedARuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
                decisionA3.DefinitionActorId,
                decisionA3.CandidateRevision,
                "worker-a-evolved-" + context.RunId,
                ct);
            await context.Capabilities.RunScriptInstanceAsync(
                evolvedARuntimeId,
                "worker-a-evolved-run-" + context.RunId,
                Any.Pack(new Struct()),
                decisionA3.CandidateRevision,
                decisionA3.DefinitionActorId,
                "worker.a.evolved",
                ct);
        }

        var evolvedBRuntimeId = string.Empty;
        if (decisionB3.Accepted)
        {
            evolvedBRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
                decisionB3.DefinitionActorId,
                decisionB3.CandidateRevision,
                "worker-b-evolved-" + context.RunId,
                ct);
            await context.Capabilities.RunScriptInstanceAsync(
                evolvedBRuntimeId,
                "worker-b-evolved-run-" + context.RunId,
                Any.Pack(new Struct()),
                decisionB3.CandidateRevision,
                decisionB3.DefinitionActorId,
                "worker.b.evolved",
                ct);
        }

        var generatedDefinitionActorId2 = await context.Capabilities.UpsertScriptDefinitionAsync(
            "generated-script-2",
            "rev-g-2",
            generatedSource2,
            "hash-generated-2",
            "generated-definition-2-" + context.RunId,
            ct);
        var generatedRuntimeId2 = await context.Capabilities.SpawnScriptRuntimeAsync(
            generatedDefinitionActorId2,
            "rev-g-2",
            "generated-runtime-2-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            generatedRuntimeId2,
            "generated-run-2-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-g-2",
            generatedDefinitionActorId2,
            "generated.script.2.run",
            ct);

        await context.Capabilities.DestroyAgentAsync(lifecycleActorId, ct);

        var summary = new Struct
        {
            Fields =
            {
                ["lifecycle_actor_id"] = Value.ForString(lifecycleActorId),
                ["temp_a_runtime_id"] = Value.ForString(tempARuntimeId),
                ["temp_b_runtime_id"] = Value.ForString(tempBRuntimeId),
                ["generated_runtime_id_1"] = Value.ForString(generatedRuntimeId1),
                ["generated_runtime_id_2"] = Value.ForString(generatedRuntimeId2),
                ["evolved_a_runtime_id"] = Value.ForString(evolvedARuntimeId),
                ["evolved_b_runtime_id"] = Value.ForString(evolvedBRuntimeId),
                ["decision_a2"] = Value.ForString(decisionA2.Status),
                ["decision_a3"] = Value.ForString(decisionA3.Status),
                ["decision_b2"] = Value.ForString(decisionB2.Status),
                ["decision_b3"] = Value.ForString(decisionB3.Status),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "MultiScriptOrchestrationCompletedEvent" },
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

    private const string SelfEvolutionV1Source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class SelfEvolutionV1Runtime : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var nextV2Source = input.Fields["next_v2_source"].StringValue;
        var nextV3Source = input.Fields["next_v3_source"].StringValue;
        var generatedSource = input.Fields["generated_source"].StringValue;

        var generatedDefinitionActorId = await context.Capabilities!.UpsertScriptDefinitionAsync(
            "self-generated-script",
            "rev-self-generated-1",
            generatedSource,
            "hash-self-generated-1",
            "self-generated-definition-" + context.RunId,
            ct);
        var generatedRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            generatedDefinitionActorId,
            "rev-self-generated-1",
            "self-generated-runtime-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            generatedRuntimeId,
            "self-generated-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-self-generated-1",
            generatedDefinitionActorId,
            "self.generated.run",
            ct);

        var decisionV2 = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "self-proposal-v2-" + context.RunId,
                ScriptId: "self-evolving-script",
                BaseRevision: "rev-self-1",
                CandidateRevision: "rev-self-2",
                CandidateSource: nextV2Source,
                CandidateSourceHash: string.Empty,
                Reason: "self evolution to rev-self-2"),
            ct);

        var v2RuntimeId = string.Empty;
        if (decisionV2.Accepted)
        {
            v2RuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
                decisionV2.DefinitionActorId,
                decisionV2.CandidateRevision,
                "self-v2-runtime-" + context.RunId,
                ct);
            await context.Capabilities.RunScriptInstanceAsync(
                v2RuntimeId,
                "self-v2-run-" + context.RunId,
                Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["next_v3_source"] = Value.ForString(nextV3Source),
                    },
                }),
                decisionV2.CandidateRevision,
                decisionV2.DefinitionActorId,
                "self.v2.run",
                ct);
        }

        var summary = new Struct
        {
            Fields =
            {
                ["decision_v2"] = Value.ForString(decisionV2.Status),
                ["v2_runtime_id"] = Value.ForString(v2RuntimeId),
                ["generated_runtime_id"] = Value.ForString(generatedRuntimeId),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "SelfEvolutionV1CompletedEvent" },
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

    private const string SelfEvolutionV2Source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class SelfEvolutionV2Runtime : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var nextV3Source = input.Fields["next_v3_source"].StringValue;

        var decisionV3 = await context.Capabilities!.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "self-proposal-v3-" + context.RunId,
                ScriptId: "self-evolving-script",
                BaseRevision: "rev-self-2",
                CandidateRevision: "rev-self-3",
                CandidateSource: nextV3Source,
                CandidateSourceHash: string.Empty,
                Reason: "self evolution to rev-self-3"),
            ct);

        var v3RuntimeId = string.Empty;
        if (decisionV3.Accepted)
        {
            v3RuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
                decisionV3.DefinitionActorId,
                decisionV3.CandidateRevision,
                "self-v3-runtime-" + context.RunId,
                ct);
            await context.Capabilities.RunScriptInstanceAsync(
                v3RuntimeId,
                "self-v3-run-" + context.RunId,
                Any.Pack(new Struct()),
                decisionV3.CandidateRevision,
                decisionV3.DefinitionActorId,
                "self.v3.run",
                ct);
        }

        var summary = new Struct
        {
            Fields =
            {
                ["decision_v3"] = Value.ForString(decisionV3.Status),
                ["v3_runtime_id"] = Value.ForString(v3RuntimeId),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "SelfEvolutionV2CompletedEvent" },
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

    private const string SelfEvolutionV3Source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class SelfEvolutionV3Runtime : IScriptPackageRuntime
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
                new StringValue { Value = "SelfEvolutionV3CompletedEvent" },
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

    private const string CatalogControlOrchestratorSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class CatalogControlOrchestrator : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var manualV1Source = input.Fields["manual_v1_source"].StringValue;
        var manualV2Source = input.Fields["manual_v2_source"].StringValue;

        var manualDefinitionActorIdV1 = await context.Capabilities!.UpsertScriptDefinitionAsync(
            "manual-catalog-script",
            "rev-manual-1",
            manualV1Source,
            "hash-manual-1",
            "manual-catalog-definition-v1",
            ct);
        await context.Capabilities.PromoteRevisionAsync(
            "script-catalog",
            "manual-catalog-script",
            "rev-manual-1",
            manualDefinitionActorIdV1,
            "hash-manual-1",
            "manual-promote-v1-" + context.RunId,
            ct);

        var manualDefinitionActorIdV2 = await context.Capabilities.UpsertScriptDefinitionAsync(
            "manual-catalog-script",
            "rev-manual-2",
            manualV2Source,
            "hash-manual-2",
            "manual-catalog-definition-v2",
            ct);
        await context.Capabilities.PromoteRevisionAsync(
            "script-catalog",
            "manual-catalog-script",
            "rev-manual-2",
            manualDefinitionActorIdV2,
            "hash-manual-2",
            "manual-promote-v2-" + context.RunId,
            ct);

        var manualRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            manualDefinitionActorIdV2,
            "rev-manual-2",
            "manual-catalog-runtime-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            manualRuntimeId,
            "manual-catalog-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-manual-2",
            manualDefinitionActorIdV2,
            "manual.catalog.run",
            ct);

        await context.Capabilities.RollbackRevisionAsync(
            "script-catalog",
            "manual-catalog-script",
            "rev-manual-1",
            "rollback by script capability",
            "manual-rollback-" + context.RunId,
            ct);

        var summary = new Struct
        {
            Fields =
            {
                ["manual_definition_actor_id_v1"] = Value.ForString(manualDefinitionActorIdV1),
                ["manual_definition_actor_id_v2"] = Value.ForString(manualDefinitionActorIdV2),
                ["manual_runtime_id"] = Value.ForString(manualRuntimeId),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "CatalogControlCompletedEvent" },
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

    private const string InteractionUpsertOrchestratorSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class InteractionUpsertOrchestrator : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var definitionType = input.Fields["definition_agent_type"].StringValue;
        var publishSource = input.Fields["publish_source"].StringValue;
        var sendToSource = input.Fields["sendto_source"].StringValue;
        var invokeSource = input.Fields["invoke_source"].StringValue;

        var aiResponse = await context.Capabilities!.AskAIAsync("health-check", ct);

        var publishedDefinitionActorId = await context.Capabilities.CreateAgentAsync(
            definitionType,
            "published-definition-" + context.RunId,
            ct);
        await context.Capabilities.LinkAgentsAsync(context.ActorId, publishedDefinitionActorId, ct);
        await context.Capabilities.PublishAsync(
            new StringValue
            {
                Value = "interaction.publish.signal",
            },
            TopologyAudience.Children,
            ct);
        await context.Capabilities.UnlinkAgentAsync(publishedDefinitionActorId, ct);

        var sendToDefinitionActorId = await context.Capabilities.CreateAgentAsync(
            definitionType,
            "sendto-definition-" + context.RunId,
            ct);
        await context.Capabilities.SendToAsync(
            sendToDefinitionActorId,
            new Aevatar.Scripting.Abstractions.UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = "interaction-sendto-script",
                ScriptRevision = "rev-sendto-1",
                SourceText = sendToSource,
                SourceHash = "hash-sendto-1",
            },
            ct);

        var upsertDefinitionActorId = await context.Capabilities.CreateAgentAsync(
            definitionType,
            "upsert-definition-" + context.RunId,
            ct);
        upsertDefinitionActorId = await context.Capabilities.UpsertScriptDefinitionAsync(
            "interaction-invoke-script",
            "rev-invoke-1",
            invokeSource,
            "hash-invoke-1",
            upsertDefinitionActorId,
            ct);

        var summary = new Struct
        {
            Fields =
            {
                ["ai_response_length"] = Value.ForString((aiResponse ?? string.Empty).Length.ToString()),
                ["published_definition_actor_id"] = Value.ForString(publishedDefinitionActorId),
                ["sendto_definition_actor_id"] = Value.ForString(sendToDefinitionActorId),
                ["upsert_definition_actor_id"] = Value.ForString(upsertDefinitionActorId),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "InteractionInvocationCompletedEvent" },
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
