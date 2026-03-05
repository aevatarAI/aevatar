using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimReplayTests
{
    [Fact]
    public async Task Should_recompile_from_definition_source_without_external_repository()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "claim-recompile-definition";
        const string runtimeActorId = "claim-recompile-runtime";
        const string scriptRevision = "rev-claim-recompile-1";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var persistedDefinitionSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class PersistedDefinitionSourceScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "ClaimApprovedEvent" } },
            new Dictionary<string, Any>
            {
                ["claim"] = Any.Pack(new Struct
                {
                    Fields = { ["source_marker"] = Google.Protobuf.WellKnownTypes.Value.ForString("definition-source-v1") },
                }),
            },
            new Dictionary<string, Any>
            {
                ["claim_case"] = Any.Pack(new Struct
                {
                    Fields = { ["decision_status"] = Google.Protobuf.WellKnownTypes.Value.ForString("Approved") },
                }),
            }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(new Dictionary<string, Any>());

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(new Dictionary<string, Any>());
}
""";

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "claim-recompile-script",
                    ScriptRevision: scriptRevision,
                    SourceText: persistedDefinitionSource,
                    SourceHash: "hash-claim-recompile-v1"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-recompile-1",
                    InputPayload: Any.Pack(new Struct()),
                    ScriptRevision: scriptRevision,
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var firstRunState = ((ScriptRuntimeGAgent)runtimeActor.Agent).State;
        firstRunState.StatePayloads.Should().ContainKey("claim");
        firstRunState.StatePayloads["claim"]
            .Unpack<Struct>()
            .Fields["source_marker"]
            .StringValue
            .Should()
            .Be("definition-source-v1");

        var externalUpdatedSourceButNotPersisted = persistedDefinitionSource.Replace(
            "definition-source-v1",
            "definition-source-v2",
            StringComparison.Ordinal);
        externalUpdatedSourceButNotPersisted.Should().Contain("definition-source-v2");

        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-recompile-2",
                    InputPayload: Any.Pack(new Struct()),
                    ScriptRevision: scriptRevision,
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var secondRunState = ((ScriptRuntimeGAgent)runtimeActor.Agent).State;
        secondRunState.StatePayloads.Should().ContainKey("claim");
        secondRunState.StatePayloads["claim"]
            .Unpack<Struct>()
            .Fields["source_marker"]
            .StringValue
            .Should()
            .Be("definition-source-v1");
    }

    [Fact]
    public async Task Should_rebuild_same_state_from_event_stream()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "claim-replay-definition";
        const string runtimeActorId = "claim-replay-runtime";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "claim-script",
                    ScriptRevision: "rev-claim-replay",
                    SourceText: """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimReplayScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        var caseId = requestedEvent.Payload != null && requestedEvent.Payload.Is(Struct.Descriptor)
            ? requestedEvent.Payload.Unpack<Struct>().Fields.TryGetValue("caseId", out var field) ? field.StringValue : string.Empty
            : string.Empty;
        var evt = string.Equals(caseId, "Case-B", StringComparison.Ordinal)
            ? "ClaimManualReviewRequestedEvent"
            : "ClaimApprovedEvent";
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = evt } }));
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
                    SourceHash: "hash-claim-replay"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-replay-case-b",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B") },
                    }),
                    ScriptRevision: "rev-claim-replay",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var before = (ScriptRuntimeGAgent)runtimeActor.Agent;
        var beforeState = before.State.Clone();

        await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
        var replayedActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
        var replayed = (ScriptRuntimeGAgent)replayedActor.Agent;

        replayed.State.Revision.Should().Be(beforeState.Revision);
        replayed.State.LastRunId.Should().Be(beforeState.LastRunId);
        replayed.State.StatePayloads.Should().BeEquivalentTo(beforeState.StatePayloads);
        replayed.State.LastAppliedEventVersion.Should().Be(beforeState.LastAppliedEventVersion);
    }

    [Fact]
    public async Task Should_rebuild_same_readmodel_from_event_stream()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string definitionActorId = "claim-readmodel-definition";
        const string runtimeActorId = "claim-readmodel-runtime";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);

        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: "claim-script-rm",
                    ScriptRevision: "rev-claim-rm",
                    SourceText: """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimReadModelScript : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "ClaimManualReviewRequestedEvent" } }));
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
                    SourceHash: "hash-claim-rm"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptActorRequestAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptActorRequest(
                    RunId: "run-readmodel",
                    InputPayload: Any.Pack(new Struct
                    {
                        Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B") },
                    }),
                    ScriptRevision: "rev-claim-rm",
                    DefinitionActorId: definitionActorId),
                runtimeActorId),
            CancellationToken.None);

        var persisted = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
        var committedEvents = persisted
            .Where(x => x.EventData?.Is(ScriptRunDomainEventCommitted.Descriptor) == true)
            .Select(x => new EventEnvelope
            {
                Id = x.EventId,
                Timestamp = x.Timestamp,
                Payload = x.EventData,
                PublisherId = runtimeActorId,
                Direction = EventDirection.Self,
                CorrelationId = "run-readmodel",
            })
            .ToArray();

        var context = new ScriptProjectionContext
        {
            ProjectionId = "projection-claim-readmodel",
            RootActorId = runtimeActorId,
            ScriptId = "claim-script-rm",
        };

        var dispatcher1 = new InMemoryScriptProjectionStoreDispatcher();
        var projector1 = new ScriptExecutionReadModelProjector(
            dispatcher1,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        await projector1.InitializeAsync(context, CancellationToken.None);
        foreach (var envelope in committedEvents)
            await projector1.ProjectAsync(context, envelope, CancellationToken.None);
        var readModel1 = await dispatcher1.GetAsync(runtimeActorId, CancellationToken.None);

        var dispatcher2 = new InMemoryScriptProjectionStoreDispatcher();
        var projector2 = new ScriptExecutionReadModelProjector(
            dispatcher2,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        await projector2.InitializeAsync(context, CancellationToken.None);
        foreach (var envelope in committedEvents)
            await projector2.ProjectAsync(context, envelope, CancellationToken.None);
        var readModel2 = await dispatcher2.GetAsync(runtimeActorId, CancellationToken.None);

        readModel2.Should().BeEquivalentTo(readModel1);
    }

    private sealed class InMemoryScriptProjectionStoreDispatcher
        : IProjectionStoreDispatcher<ScriptExecutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptExecutionReadModel> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptExecutionReadModel readModel, CancellationToken ct = default)
        {
            _store[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<ScriptExecutionReadModel> mutate, CancellationToken ct = default)
        {
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptExecutionReadModel { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptExecutionReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptExecutionReadModel>> ListAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScriptExecutionReadModel>>(_store.Values.Take(take).ToArray());
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
