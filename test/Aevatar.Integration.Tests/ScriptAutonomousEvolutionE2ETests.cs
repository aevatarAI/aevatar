using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Slow")]
public class ScriptAutonomousEvolutionE2ETests
{
    [Fact]
    public async Task ScriptOnlyFlow_ShouldSpawnTempAndNewAgents_AndPromoteEvolution()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string workerDefinitionActorId = "worker-definition";
        const string orchestratorDefinitionActorId = "orchestrator-definition";
        const string orchestratorRuntimeActorId = "orchestrator-runtime";

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        var run = new RunScriptActorRequestAdapter();

        var workerDefinition = await runtime.CreateAsync<ScriptDefinitionGAgent>(workerDefinitionActorId);
        await workerDefinition.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "worker-script",
                    ScriptRevision: "rev-worker-1",
                    SourceText: WorkerRuntimeV1Source,
                    SourceHash: "hash-worker-1"),
                workerDefinitionActorId),
            CancellationToken.None);

        var orchestratorDefinition = await runtime.CreateAsync<ScriptDefinitionGAgent>(orchestratorDefinitionActorId);
        await orchestratorDefinition.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "orchestrator-script",
                    ScriptRevision: "rev-orchestrator-1",
                    SourceText: ScriptOnlyOrchestratorSource,
                    SourceHash: "hash-orchestrator-1"),
                orchestratorDefinitionActorId),
            CancellationToken.None);

        var orchestratorRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(orchestratorRuntimeActorId);
        await orchestratorRuntime.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-autonomy-1",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields =
                        {
                            ["newScriptSource"] = Google.Protobuf.WellKnownTypes.Value.ForString(NewRuntimeSource),
                            ["workerV2Source"] = Google.Protobuf.WellKnownTypes.Value.ForString(WorkerRuntimeV2Source),
                        },
                    }),
                    ScriptRevision: "rev-orchestrator-1",
                    DefinitionActorId: orchestratorDefinitionActorId,
                    RequestedEventType: "script.autonomous.orchestrate"),
                orchestratorRuntimeActorId),
            CancellationToken.None);

        var summary = ((ScriptRuntimeGAgent)orchestratorRuntime.Agent)
            .State
            .StatePayloads["summary"]
            .Unpack<Struct>();

        var tempRuntimeId = summary.Fields["temp_runtime_id"].StringValue;
        var newRuntimeId = summary.Fields["new_runtime_id"].StringValue;
        var evolvedRuntimeId = summary.Fields["evolved_runtime_id"].StringValue;
        var newDefinitionActorId = summary.Fields["new_definition_actor_id"].StringValue;
        var decisionStatus = summary.Fields["decision_status"].StringValue;

        decisionStatus.Should().Be("promoted");
        (await runtime.ExistsAsync(tempRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(newRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(evolvedRuntimeId)).Should().BeTrue();
        (await runtime.ExistsAsync(newDefinitionActorId)).Should().BeTrue();

        var manager = ((ScriptEvolutionManagerGAgent)(await runtime.GetAsync("script-evolution-manager"))!.Agent);
        manager.State.Proposals.Should().ContainKey("proposal-run-autonomy-1");
        manager.State.Proposals["proposal-run-autonomy-1"].Status.Should().Be("promoted");

        var catalog = ((ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent);
        catalog.State.Entries.Should().ContainKey("worker-script");
        catalog.State.Entries["worker-script"].ActiveRevision.Should().Be("rev-worker-2");
        catalog.State.Entries["worker-script"].ActiveDefinitionActorId.Should().Be("script-definition:worker-script");

        var promotedWorkerDefinitionActorId = catalog.State.Entries["worker-script"].ActiveDefinitionActorId;
        var promotedWorkerDefinition = (ScriptDefinitionGAgent)(await runtime.GetAsync(promotedWorkerDefinitionActorId))!.Agent;
        promotedWorkerDefinition.State.Revision.Should().Be("rev-worker-2");

        var evolvedRuntime = (ScriptRuntimeGAgent)(await runtime.GetAsync(evolvedRuntimeId))!.Agent;
        evolvedRuntime.State.Revision.Should().Be("rev-worker-2");

        var events = await eventStore.GetEventsAsync(orchestratorRuntimeActorId, ct: CancellationToken.None);
        events.Should().Contain(x => x.EventData != null && x.EventData.Is(ScriptRunDomainEventCommitted.Descriptor));
    }

    private const string ScriptOnlyOrchestratorSource = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptOnlyOrchestrator : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = requestedEvent.Payload.Unpack<Struct>();
        var newScriptSource = input.Fields["newScriptSource"].StringValue;
        var workerV2Source = input.Fields["workerV2Source"].StringValue;

        var tempRuntimeId = await context.Capabilities!.SpawnScriptRuntimeAsync(
            "worker-definition",
            "rev-worker-1",
            "worker-temp-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            tempRuntimeId,
            "worker-temp-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-worker-1",
            "worker-definition",
            "worker.temp.run",
            ct);

        var newDefinitionActorId = await context.Capabilities.UpsertScriptDefinitionAsync(
            "new-script",
            "rev-new-1",
            newScriptSource,
            "hash-new-1",
            "new-definition",
            ct);
        var newRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
            newDefinitionActorId,
            "rev-new-1",
            "new-runtime-" + context.RunId,
            ct);
        await context.Capabilities.RunScriptInstanceAsync(
            newRuntimeId,
            "new-run-" + context.RunId,
            Any.Pack(new Struct()),
            "rev-new-1",
            newDefinitionActorId,
            "new.runtime.run",
            ct);

        var decision = await context.Capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-" + context.RunId,
                ScriptId: "worker-script",
                BaseRevision: "rev-worker-1",
                CandidateRevision: "rev-worker-2",
                CandidateSource: workerV2Source,
                CandidateSourceHash: "hash-worker-2",
                Reason: "script-only autonomous promotion"),
            ct);

        var evolvedRuntimeId = string.Empty;
        if (decision.Accepted)
        {
            evolvedRuntimeId = await context.Capabilities.SpawnScriptRuntimeAsync(
                decision.DefinitionActorId,
                decision.CandidateRevision,
                "worker-evolved-" + context.RunId,
                ct);
            await context.Capabilities.RunScriptInstanceAsync(
                evolvedRuntimeId,
                "worker-evolved-run-" + context.RunId,
                Any.Pack(new Struct()),
                decision.CandidateRevision,
                decision.DefinitionActorId,
                "worker.evolved.run",
                ct);
        }

        var summary = new Struct
        {
            Fields =
            {
                ["temp_runtime_id"] = Value.ForString(tempRuntimeId),
                ["new_runtime_id"] = Value.ForString(newRuntimeId),
                ["evolved_runtime_id"] = Value.ForString(evolvedRuntimeId),
                ["new_definition_actor_id"] = Value.ForString(newDefinitionActorId),
                ["decision_status"] = Value.ForString(decision.Status),
            },
        };

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "ScriptOnlyOrchestrationCompleted" },
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

    private const string WorkerRuntimeV1Source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class WorkerScriptV1 : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "WorkerV1CompletedEvent" } }));
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

    private const string WorkerRuntimeV2Source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class WorkerScriptV2 : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "WorkerV2CompletedEvent" } }));
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

    private const string NewRuntimeSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class NewRuntimeScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "NewScriptCompletedEvent" } }));
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
