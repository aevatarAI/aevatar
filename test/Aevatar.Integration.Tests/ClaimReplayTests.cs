using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Application;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimReplayTests
{
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

        var upsert = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: "claim-script",
                    ScriptRevision: "rev-claim-replay",
                    SourceText: """
using System.Collections.Generic;
public static class ClaimReplayScript
{
    public static IReadOnlyList<string> Decide(string inputJson) =>
        inputJson.Contains("Case-B") ? new[] { "ClaimManualReviewRequestedEvent" } : new[] { "ClaimApprovedEvent" };
}
""",
                    SourceHash: "hash-claim-replay"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptCommandAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptCommand(
                    RunId: "run-replay-case-b",
                    InputJson: "{\"caseId\":\"Case-B\"}",
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
        replayed.State.StatePayloadJson.Should().Be(beforeState.StatePayloadJson);
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

        var upsert = new UpsertScriptDefinitionCommandAdapter();
        await definitionActor.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: "claim-script-rm",
                    ScriptRevision: "rev-claim-rm",
                    SourceText: """
using System.Collections.Generic;
public static class ClaimReadModelScript
{
    public static IReadOnlyList<string> Decide(string inputJson) => new[] { "ClaimManualReviewRequestedEvent" };
}
""",
                    SourceHash: "hash-claim-rm"),
                definitionActorId),
            CancellationToken.None);

        var run = new RunScriptCommandAdapter();
        await runtimeActor.HandleEventAsync(
            run.Map(
                new RunScriptCommand(
                    RunId: "run-readmodel",
                    InputJson: "{\"caseId\":\"Case-B\"}",
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
