using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Slow")]
public sealed class ScriptAutonomousEvolutionE2ETests
{
    [Fact]
    public async Task ScriptOnlyFlow_ShouldSpawnTempAndNewAgents_AndPromoteEvolution()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string workerDefinitionActorId = "worker-definition";
        const string orchestratorDefinitionActorId = "orchestrator-definition";
        const string orchestratorRuntimeActorId = "orchestrator-runtime";

        var workerV1Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "WorkerScriptV1",
            "WORKER-V1",
            "worker_normalization",
            "1");
        var workerV2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "WorkerScriptV2",
            "WORKER-V2",
            "worker_normalization",
            "2");
        var newRuntimeSource = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "NewRuntimeScript",
            "NEW-V1",
            "new_runtime_normalization",
            "1");

        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            scriptId: "worker-script",
            revision: "rev-worker-1",
            sourceText: workerV1Source,
            definitionActorId: workerDefinitionActorId,
            ct: CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.UpsertDefinitionAsync(
            provider,
            scriptId: "orchestrator-script",
            revision: "rev-orchestrator-1",
            sourceText: ScriptEvolutionIntegrationSources.ScriptOnlyOrchestratorSource,
            definitionActorId: orchestratorDefinitionActorId,
            ct: CancellationToken.None);
        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            orchestratorDefinitionActorId,
            "rev-orchestrator-1",
            orchestratorRuntimeActorId,
            CancellationToken.None);

        var input = new ScriptOnlyEvolutionRequested
        {
            NewScriptSource = newRuntimeSource,
            WorkerV2Source = workerV2Source,
        };

        var (fact, snapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            runtimeActorId: orchestratorRuntimeActorId,
            runId: "run-autonomy-1",
            inputPayload: Any.Pack(input),
            revision: "rev-orchestrator-1",
            definitionActorId: orchestratorDefinitionActorId,
            requestedEventType: "script.autonomous.orchestrate",
            ct: CancellationToken.None);

        fact.DomainEventPayload.Should().NotBeNull();
        fact.DomainEventPayload!.Is(ScriptOnlyEvolutionCompleted.Descriptor).Should().BeTrue();

        snapshot.ReadModelPayload.Should().NotBeNull();
        var summary = snapshot.ReadModelPayload!.Unpack<ScriptOnlyEvolutionState>();

        var tempRuntimeId = summary.TempRuntimeId;
        var newRuntimeId = summary.NewRuntimeId;
        var evolvedRuntimeId = summary.EvolvedRuntimeId;
        var newDefinitionActorId = summary.NewDefinitionActorId;
        var decisionStatus = summary.DecisionStatus;

        decisionStatus.Should().Be("promoted");
        (await runtime.ExistsAsync(tempRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(newRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(newDefinitionActorId)).Should().BeTrue();

        var tempRuntimeSnapshot = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            tempRuntimeId,
            "temp-query",
            CancellationToken.None);
        tempRuntimeSnapshot.NormalizedText.Should().Be("WORKER-V1:TEMP");

        var newRuntimeSnapshot = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            newRuntimeId,
            "new-query",
            CancellationToken.None);
        newRuntimeSnapshot.NormalizedText.Should().Be("NEW-V1:NEW");

        var evolvedRuntimeSnapshot = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            evolvedRuntimeId,
            "evolved-query",
            CancellationToken.None);
        evolvedRuntimeSnapshot.NormalizedText.Should().Be("WORKER-V2:EVOLVED");

        var managerActor = await runtime.GetAsync("script-evolution-manager");
        managerActor.Should().NotBeNull();
        var manager = managerActor!.Agent.Should().BeOfType<ScriptEvolutionManagerGAgent>().Subject;
        manager.State.Proposals.Should().ContainKey("proposal-run-autonomy-1");
        manager.State.Proposals["proposal-run-autonomy-1"].Status.Should().Be("promoted");

        var catalogEntry = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "worker-script",
            CancellationToken.None,
            expectedRevision: "rev-worker-2");
        catalogEntry.Should().NotBeNull();
        catalogEntry!.ActiveRevision.Should().Be("rev-worker-2");
        catalogEntry.ActiveDefinitionActorId.Should().Be("script-definition:worker-script");

        var promotedWorkerDefinition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            catalogEntry.ActiveDefinitionActorId,
            "rev-worker-2",
            CancellationToken.None);
        promotedWorkerDefinition.ScriptId.Should().Be("worker-script");
        promotedWorkerDefinition.Revision.Should().Be("rev-worker-2");

        var events = await eventStore.GetEventsAsync(orchestratorRuntimeActorId, ct: CancellationToken.None);
        events.Should().Contain(x => x.EventData != null && x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
    }
}
