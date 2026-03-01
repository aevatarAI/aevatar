using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Application;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ScriptDefinitionRuntimeContractTests
{
    [Fact]
    public async Task Runtime_should_compile_from_definition_snapshot_only()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        var definitionActorId = "contract-definition";
        var runtimeActorId = "contract-runtime";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsert = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: "script-contract",
                    ScriptRevision: "rev-contract-1",
                    SourceText: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ContractScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "FromDefinitionSnapshotEvent" } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"last_event\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""",
                    SourceHash: "hash-contract-1"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptCommandAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptCommand(
                    RunId: "run-contract-1",
                    InputJson: "{\"caseId\":\"Case-A\"}",
                    ScriptRevision: "rev-contract-1",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var persisted = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
        var committed = persisted
            .Where(x => x.EventData?.Is(ScriptRunDomainEventCommitted.Descriptor) == true)
            .Select(x => x.EventData!.Unpack<ScriptRunDomainEventCommitted>())
            .ToList();

        committed.Should().NotBeEmpty();
        committed.Should().Contain(x => x.EventType == "FromDefinitionSnapshotEvent");
    }

    [Fact]
    public async Task Replay_should_succeed_without_external_script_repository()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "self-contained-definition";
        const string runtimeActorId = "self-contained-runtime";

        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsert = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: "script-self-contained",
                    ScriptRevision: "rev-self-contained",
                    SourceText: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ReplayScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "ReplaySelfContainedEvent" } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"last_event\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""",
                    SourceHash: "hash-self-contained"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptCommandAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptCommand(
                    RunId: "run-self-contained",
                    InputJson: "{\"caseId\":\"Case-B\"}",
                    ScriptRevision: "rev-self-contained",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var before = (ScriptRuntimeGAgent)runtimeActor.Agent;
        var beforeStateJson = before.State.StatePayloadJson;
        var beforeRevision = before.State.Revision;
        var beforeRunId = before.State.LastRunId;

        await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
        await runtime.DestroyAsync(definitionActorId, CancellationToken.None);

        var replayedRuntimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
        var replayed = (ScriptRuntimeGAgent)replayedRuntimeActor.Agent;

        replayed.State.Revision.Should().Be(beforeRevision);
        replayed.State.LastRunId.Should().Be(beforeRunId);
        replayed.State.StatePayloadJson.Should().Be(beforeStateJson);
    }

    [Fact]
    public async Task Runtime_should_reject_revision_mismatch_against_definition_snapshot()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "revision-check-definition";
        const string runtimeActorId = "revision-check-runtime";

        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsert = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: "script-revision-check",
                    ScriptRevision: "rev-actual",
                    SourceText: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class RevisionScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "RevisionEvent" } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"last_event\":\"" + domainEvent.EventType + "\"}");

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct) =>
        ValueTask.FromResult("{\"decision\":\"" + domainEvent.EventType + "\"}");
}
""",
                    SourceHash: "hash-revision"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptCommandAdapter();
        Func<Task> act = async () => await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptCommand(
                    RunId: "run-revision-check",
                    InputJson: "{}",
                    ScriptRevision: "rev-requested-mismatch",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision*");
    }
}
