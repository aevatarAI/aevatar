using Aevatar.Foundation.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using PbValue = Google.Protobuf.WellKnownTypes.Value;

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
        var input = new Struct
        {
            Fields =
            {
                ["worker_a_v2_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "WorkerARev2Runtime",
                    "WORKER-A-V2",
                    "worker_a",
                    "2")),
                ["worker_a_v3_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "WorkerARev3Runtime",
                    "WORKER-A-V3",
                    "worker_a",
                    "3")),
                ["worker_b_v2_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "WorkerBRev2Runtime",
                    "WORKER-B-V2",
                    "worker_b",
                    "2")),
                ["worker_b_v3_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "WorkerBRev3Runtime",
                    "WORKER-B-V3",
                    "worker_b",
                    "3")),
                ["generated_source_1"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "GeneratedRound1Runtime",
                    "GENERATED-1",
                    "generated_round",
                    "1")),
                ["generated_source_2"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "GeneratedRound2Runtime",
                    "GENERATED-2",
                    "generated_round",
                    "2")),
                ["runtime_agent_type"] = PbValue.ForString(runtimeAgentType),
            },
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

        var summary = snapshot.ReadModelPayload!.Unpack<Struct>();
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

        var input = new Struct
        {
            Fields =
            {
                ["next_v2_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.SelfEvolutionV2Source),
                ["next_v3_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "SelfEvolutionV3Runtime",
                    "SELF-V3",
                    "self_evolution",
                    "3")),
                ["generated_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "SelfGeneratedRuntime",
                    "SELF-GEN",
                    "self_generated",
                    "1")),
            },
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

        var v1Summary = rootSnapshot.ReadModelPayload!.Unpack<Struct>();
        v1Summary.Fields["decision_v2"].StringValue.Should().Be("promoted");
        var v2RuntimeId = v1Summary.Fields["v2_runtime_id"].StringValue;
        var generatedRuntimeId = v1Summary.Fields["generated_runtime_id"].StringValue;
        (await runtime.ExistsAsync(v2RuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(generatedRuntimeId)).Should().BeTrue();

        var generatedResult = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            generatedRuntimeId,
            "generated-self-query",
            CancellationToken.None);
        generatedResult.NormalizedText.Should().Be("SELF-GEN:GENERATED");

        var v2Summary = await ScriptEvolutionIntegrationTestKit.GetSummaryAsync(
            provider,
            v2RuntimeId,
            CancellationToken.None);
        v2Summary.Fields["decision_v3"].StringValue.Should().Be("promoted");
        var v3RuntimeId = v2Summary.Fields["v3_runtime_id"].StringValue;
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

        var input = new Struct
        {
            Fields =
            {
                ["manual_v1_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "ManualCatalogRev1Runtime",
                    "MANUAL-V1",
                    "manual_catalog",
                    "1")),
                ["manual_v2_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "ManualCatalogRev2Runtime",
                    "MANUAL-V2",
                    "manual_catalog",
                    "2")),
            },
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

        var summary = snapshot.ReadModelPayload!.Unpack<Struct>();
        var manualRuntimeId = summary.Fields["manual_runtime_id"].StringValue;
        var manualDefinitionActorIdV1 = summary.Fields["manual_definition_actor_id_v1"].StringValue;
        var manualDefinitionActorIdV2 = summary.Fields["manual_definition_actor_id_v2"].StringValue;

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

        var input = new Struct
        {
            Fields =
            {
                ["definition_agent_type"] = PbValue.ForString(definitionType),
                ["publish_source"] = PbValue.ForString(ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
                    "PublishedDefinitionRuntime",
                    "PUBLISHED",
                    "interaction_published",
                    "1")),
                ["sendto_source"] = PbValue.ForString(sendToSource),
                ["invoke_source"] = PbValue.ForString(invokeSource),
            },
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

        var summary = snapshot.ReadModelPayload!.Unpack<Struct>();
        summary.Fields["ai_response_length"].StringValue.Should().Be("0");

        var publishedDefinitionId = summary.Fields["published_definition_actor_id"].StringValue;
        var sendToDefinitionId = summary.Fields["sendto_definition_actor_id"].StringValue;
        var upsertDefinitionId = summary.Fields["upsert_definition_actor_id"].StringValue;

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
