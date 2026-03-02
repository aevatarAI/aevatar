using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "script-contract",
                    ScriptRevision: "rev-contract-1",
                    SourceText: """
using System.Collections.Generic;
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

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct { Fields = { ["last_event"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""",
                    SourceHash: "hash-contract-1"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-contract-1",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-A") },
                    }),
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

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "script-self-contained",
                    ScriptRevision: "rev-self-contained",
                    SourceText: """
using System.Collections.Generic;
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

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct { Fields = { ["last_event"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""",
                    SourceHash: "hash-self-contained"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-self-contained",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B") },
                    }),
                    ScriptRevision: "rev-self-contained",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var before = (ScriptRuntimeGAgent)runtimeActor.Agent;
        var beforeStatePayloads = before.State.StatePayloads
            .ToDictionary(
                x => x.Key,
                x => x.Value.Clone(),
                StringComparer.Ordinal);
        var beforeRevision = before.State.Revision;
        var beforeRunId = before.State.LastRunId;

        await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
        await runtime.DestroyAsync(definitionActorId, CancellationToken.None);

        var replayedRuntimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
        var replayed = (ScriptRuntimeGAgent)replayedRuntimeActor.Agent;

        replayed.State.Revision.Should().Be(beforeRevision);
        replayed.State.LastRunId.Should().Be(beforeRunId);
        replayed.State.StatePayloads.Should().BeEquivalentTo(beforeStatePayloads);
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

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "script-revision-check",
                    ScriptRevision: "rev-actual",
                    SourceText: """
using System.Collections.Generic;
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

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Struct { Fields = { ["last_event"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new Struct { Fields = { ["decision"] = Google.Protobuf.WellKnownTypes.Value.ForString(domainEvent.EventType) } }),
            });
}
""",
                    SourceHash: "hash-revision"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        Func<Task> act = async () => await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-revision-check",
                    InputPayload: Any.Pack(new Struct()),
                    ScriptRevision: "rev-requested-mismatch",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision*");
    }
}
