using Aevatar.Foundation.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Slow")]
public sealed class ScriptAutonomousEvolutionComprehensiveE2ETests
{
    [Fact]
    public async Task ScriptOnlyFlow_ShouldDriveMultiScriptMultiRoundEvolution_AndDynamicLifecycle()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string workerADefinitionActorId = "multi-worker-a-definition";
        const string workerBDefinitionActorId = "multi-worker-b-definition";
        const string orchestratorDefinitionActorId = "multi-orchestrator-definition";
        const string orchestratorRuntimeActorId = "multi-orchestrator-runtime";

        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "worker-a-script",
            "rev-a-1",
            ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerARev1Runtime",
                "WORKER-A-V1",
                "worker_a",
                "1"),
            workerADefinitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "worker-b-script",
            "rev-b-1",
            ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerBRev1Runtime",
                "WORKER-B-V1",
                "worker_b",
                "1"),
            workerBDefinitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "multi-orchestrator-script",
            "rev-orchestrator-1",
            ScriptEvolutionIntegrationSources.MultiScriptOrchestratorSource,
            orchestratorDefinitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            orchestratorDefinitionActorId,
            "rev-orchestrator-1",
            orchestratorRuntimeActorId,
            CancellationToken.None);

        var runtimeAgentType = typeof(ScriptBehaviorGAgent).AssemblyQualifiedName
            ?? throw new InvalidOperationException("ScriptBehaviorGAgent type name is required.");
        var input = new MultiScriptEvolutionRequested
        {
            WorkerAV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerARev2Runtime",
                "WORKER-A-V2",
                "worker_a",
                "2"),
            WorkerAV3Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerARev3Runtime",
                "WORKER-A-V3",
                "worker_a",
                "3"),
            WorkerBV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerBRev2Runtime",
                "WORKER-B-V2",
                "worker_b",
                "2"),
            WorkerBV3Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "WorkerBRev3Runtime",
                "WORKER-B-V3",
                "worker_b",
                "3"),
            GeneratedSource1 = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "GeneratedRound1Runtime",
                "GENERATED-1",
                "generated_round",
                "1"),
            GeneratedSource2 = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "GeneratedRound2Runtime",
                "GENERATED-2",
                "generated_round",
                "2"),
            RuntimeAgentType = runtimeAgentType,
        };

        var (_, snapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            orchestratorRuntimeActorId,
            "run-multi-script-1",
            Any.Pack(input),
            "rev-orchestrator-1",
            orchestratorDefinitionActorId,
            "script.autonomous.multi.orchestrate",
            CancellationToken.None);

        var summary = snapshot.ReadModelPayload!.Unpack<MultiScriptEvolutionState>();
        summary.DecisionA2.Should().Be("promoted");
        summary.DecisionA3.Should().Be("promoted");
        summary.DecisionB2.Should().Be("promoted");
        summary.DecisionB3.Should().Be("promoted");

        var lifecycleActorId = summary.LifecycleActorId;
        var tempARuntimeId = summary.TempARuntimeId;
        var tempBRuntimeId = summary.TempBRuntimeId;
        var generatedRuntimeId1 = summary.GeneratedRuntimeId1;
        var generatedRuntimeId2 = summary.GeneratedRuntimeId2;
        var evolvedARuntimeId = summary.EvolvedARuntimeId;
        var evolvedBRuntimeId = summary.EvolvedBRuntimeId;

        (await runtime.ExistsAsync(lifecycleActorId)).Should().BeFalse();
        (await runtime.ExistsAsync(tempARuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(tempBRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId1)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId2)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedARuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedBRuntimeId)).Should().BeTrue();

        var workerAResult = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            evolvedARuntimeId,
            "worker-a-query",
            CancellationToken.None);
        workerAResult.NormalizedText.Should().Be("WORKER-A-V3:WORKER A EVOLVED");

        var workerBResult = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            evolvedBRuntimeId,
            "worker-b-query",
            CancellationToken.None);
        workerBResult.NormalizedText.Should().Be("WORKER-B-V3:WORKER B EVOLVED");

        var generatedRound2 = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            generatedRuntimeId2,
            "generated-query",
            CancellationToken.None);
        generatedRound2.NormalizedText.Should().Be("GENERATED-2:GENERATED TWO");

        var managerActor = await runtime.GetAsync("script-evolution-manager");
        managerActor.Should().NotBeNull();
        var manager = managerActor!.Agent.Should().BeOfType<ScriptEvolutionManagerGAgent>().Subject;
        manager.State.Proposals.Should().ContainKey("proposal-a-2-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-a-3-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-b-2-run-multi-script-1");
        manager.State.Proposals.Should().ContainKey("proposal-b-3-run-multi-script-1");

        var catalogEntryA = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "worker-a-script",
            CancellationToken.None);
        var catalogEntryB = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "worker-b-script",
            CancellationToken.None);
        catalogEntryA.Should().NotBeNull();
        catalogEntryB.Should().NotBeNull();
        catalogEntryA!.ActiveRevision.Should().Be("rev-a-3");
        catalogEntryB!.ActiveRevision.Should().Be("rev-b-3");
        catalogEntryA.RevisionHistory.Should().Contain(["rev-a-2", "rev-a-3"]);
        catalogEntryB.RevisionHistory.Should().Contain(["rev-b-2", "rev-b-3"]);

        var workerADefinition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            catalogEntryA.ActiveDefinitionActorId,
            "rev-a-3",
            CancellationToken.None);
        var workerBDefinition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            catalogEntryB.ActiveDefinitionActorId,
            "rev-b-3",
            CancellationToken.None);
        workerADefinition.Revision.Should().Be("rev-a-3");
        workerBDefinition.Revision.Should().Be("rev-b-3");
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldSelfEvolveAcrossGenerations_WithoutFrameworkChanges()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "self-evolving-definition";
        const string runtimeActorId = "self-evolving-runtime";

        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "self-evolving-script",
            "rev-self-1",
            ScriptEvolutionIntegrationSources.SelfEvolutionV1Source,
            definitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            definitionActorId,
            "rev-self-1",
            runtimeActorId,
            CancellationToken.None);

        var input = new SelfEvolutionV1Requested
        {
            NextV2Source = ScriptEvolutionIntegrationSources.SelfEvolutionV2Source,
            NextV3Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "SelfEvolutionV3Runtime",
                "SELF-V3",
                "self_evolution",
                "3"),
            GeneratedSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "SelfGeneratedRuntime",
                "SELF-GEN",
                "self_generated",
                "1"),
        };

        var (_, rootSnapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            runtimeActorId,
            "run-self-1",
            Any.Pack(input),
            "rev-self-1",
            definitionActorId,
            "script.self.evolve",
            CancellationToken.None);

        var v1Summary = rootSnapshot.ReadModelPayload!.Unpack<SelfEvolutionV1State>();
        v1Summary.DecisionV2.Should().Be("promoted");
        var v2RuntimeId = v1Summary.V2RuntimeId;
        var generatedRuntimeId = v1Summary.GeneratedRuntimeId;
        (await runtime.ExistsAsync(v2RuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId)).Should().BeTrue();

        var generatedResult = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            generatedRuntimeId,
            "generated-self-query",
            CancellationToken.None);
        generatedResult.NormalizedText.Should().Be("SELF-GEN:GENERATED");

        var v2Summary = await ScriptEvolutionIntegrationTestKit.GetStateAsync<SelfEvolutionV2State>(
            provider,
            v2RuntimeId,
            CancellationToken.None);
        v2Summary.DecisionV3.Should().Be("promoted");
        var v3RuntimeId = v2Summary.V3RuntimeId;
        (await runtime.ExistsAsync(v3RuntimeId)).Should().BeTrue();

        var v3Result = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            v3RuntimeId,
            "self-v3-query",
            CancellationToken.None);
        v3Result.NormalizedText.Should().Be("SELF-V3:SELF V3");

        var managerActor = await runtime.GetAsync("script-evolution-manager");
        managerActor.Should().NotBeNull();
        var manager = managerActor!.Agent.Should().BeOfType<ScriptEvolutionManagerGAgent>().Subject;
        manager.State.Proposals.Should().ContainKey("self-proposal-v2-run-self-1");
        manager.State.Proposals.Should().ContainKey("self-proposal-v3-self-v2-run-run-self-1");

        var catalogEntry = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "self-evolving-script",
            CancellationToken.None);
        catalogEntry.Should().NotBeNull();
        catalogEntry!.ActiveRevision.Should().Be("rev-self-3");
        catalogEntry.RevisionHistory.Should().Contain(["rev-self-2", "rev-self-3"]);
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldUseDirectCatalogPromoteAndRollback_FromCapabilities()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string controllerDefinitionActorId = "catalog-controller-definition";
        const string controllerRuntimeActorId = "catalog-controller-runtime";

        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "catalog-controller-script",
            "rev-controller-1",
            ScriptEvolutionIntegrationSources.CatalogControlOrchestratorSource,
            controllerDefinitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            controllerDefinitionActorId,
            "rev-controller-1",
            controllerRuntimeActorId,
            CancellationToken.None);

        var input = new CatalogControlRequested
        {
            ManualV1Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "ManualCatalogRev1Runtime",
                "MANUAL-V1",
                "manual_catalog",
                "1"),
            ManualV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "ManualCatalogRev2Runtime",
                "MANUAL-V2",
                "manual_catalog",
                "2"),
        };

        var (_, snapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            controllerRuntimeActorId,
            "run-catalog-control-1",
            Any.Pack(input),
            "rev-controller-1",
            controllerDefinitionActorId,
            "script.catalog.control",
            CancellationToken.None);

        var summary = snapshot.ReadModelPayload!.Unpack<CatalogControlState>();
        var manualRuntimeId = summary.ManualRuntimeId;
        var manualDefinitionActorIdV1 = summary.ManualDefinitionActorIdV1;
        var manualDefinitionActorIdV2 = summary.ManualDefinitionActorIdV2;

        (await runtime.ExistsAsync(manualRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(manualDefinitionActorIdV1)).Should().BeTrue();
        (await runtime.ExistsAsync(manualDefinitionActorIdV2)).Should().BeTrue();

        var catalogEntry = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "manual-catalog-script",
            CancellationToken.None);
        catalogEntry.Should().NotBeNull();
        catalogEntry!.ActiveRevision.Should().Be("rev-manual-1");
        catalogEntry.PreviousRevision.Should().Be("rev-manual-2");
        catalogEntry.RevisionHistory.Should().Contain(["rev-manual-1", "rev-manual-2"]);

        var definitionV1 = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            manualDefinitionActorIdV1,
            "rev-manual-1",
            CancellationToken.None);
        var definitionV2 = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            manualDefinitionActorIdV2,
            "rev-manual-2",
            CancellationToken.None);
        definitionV1.Revision.Should().Be("rev-manual-1");
        definitionV2.Revision.Should().Be("rev-manual-2");
    }

    [Fact]
    public async Task ScriptOnlyFlow_ShouldExerciseInteractionAndDefinitionUpsertCapabilities()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string controllerDefinitionActorId = "interaction-controller-definition";
        const string controllerRuntimeActorId = "interaction-controller-runtime";

        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            "interaction-controller-script",
            "rev-interaction-1",
            ScriptEvolutionIntegrationSources.InteractionUpsertOrchestratorSource,
            controllerDefinitionActorId,
            CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            controllerDefinitionActorId,
            "rev-interaction-1",
            controllerRuntimeActorId,
            CancellationToken.None);

        var definitionType = typeof(ScriptDefinitionGAgent).AssemblyQualifiedName
            ?? throw new InvalidOperationException("ScriptDefinitionGAgent type name is required.");
        var sendToSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "SendToDefinitionRuntime",
            "SEND-TO",
            "interaction_sendto",
            "1");
        var invokeSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "InvokedDefinitionRuntime",
            "INVOKED",
            "interaction_invoked",
            "1");

        var input = new InteractionUpsertRequested
        {
            DefinitionAgentType = definitionType,
            PublishSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                "PublishedDefinitionRuntime",
                "PUBLISHED",
                "interaction_published",
                "1"),
            SendtoSource = sendToSource,
            InvokeSource = invokeSource,
        };

        var (_, snapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            controllerRuntimeActorId,
            "run-interaction-1",
            Any.Pack(input),
            "rev-interaction-1",
            controllerDefinitionActorId,
            "script.interaction.exercise",
            CancellationToken.None);

        var summary = snapshot.ReadModelPayload!.Unpack<InteractionUpsertState>();
        summary.AiResponseLength.Should().Be("0");

        var publishedDefinitionId = summary.PublishedDefinitionActorId;
        var sendToDefinitionId = summary.SendtoDefinitionActorId;
        var upsertDefinitionId = summary.UpsertDefinitionActorId;

        (await runtime.ExistsAsync(publishedDefinitionId)).Should().BeTrue();
        (await runtime.ExistsAsync(sendToDefinitionId)).Should().BeTrue();
        (await runtime.ExistsAsync(upsertDefinitionId)).Should().BeTrue();

        var sendToDefinition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            sendToDefinitionId,
            "rev-sendto-1",
            CancellationToken.None);
        sendToDefinition.ScriptId.Should().Be("interaction-sendto-script");
        sendToDefinition.Revision.Should().Be("rev-sendto-1");
        sendToDefinition.SourceHash.Should().Be(ScriptingCommandEnvelopeTestKit.ComputeSourceHash(sendToSource).ToUpperInvariant());

        var upsertDefinition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            upsertDefinitionId,
            "rev-invoke-1",
            CancellationToken.None);
        upsertDefinition.ScriptId.Should().Be("interaction-invoke-script");
        upsertDefinition.Revision.Should().Be("rev-invoke-1");
        upsertDefinition.SourceHash.Should().Be(ScriptingCommandEnvelopeTestKit.ComputeSourceHash(invokeSource).ToUpperInvariant());
    }
}
