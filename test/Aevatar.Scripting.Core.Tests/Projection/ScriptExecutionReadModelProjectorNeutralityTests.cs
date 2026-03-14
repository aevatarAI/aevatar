using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ScriptExecutionReadModelProjectorNeutralityTests
{
    [Fact]
    public async Task Should_project_snapshot_payloads_without_domain_specific_inference()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "script-projection-1",
            RootActorId = "script-runtime-1",
            ScriptId = "script-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-1",
                ScriptRevision = "rev-1",
                DefinitionActorId = "definition-1",
                EventType = "AnyDomainEvent",
                Payload = Any.Pack(new StringValue { Value = "payload" }),
                StatePayloads = { ["state"] = Any.Pack(new StringValue { Value = "snapshot" }) },
                ReadModelPayloads = { ["view"] = Any.Pack(new StringValue { Value = "projection" }) },
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.LastEventType.Should().Be("AnyDomainEvent");
        readModel.StatePayloads.Should().ContainKey("state");
        readModel.StatePayloads["state"].Unpack<StringValue>().Value.Should().Be("snapshot");
        readModel.ReadModelPayloads.Should().ContainKey("view");
        readModel.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("projection");
    }

    [Fact]
    public async Task Should_not_fallback_domain_payload_to_state_snapshot_when_state_snapshot_is_empty()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "script-projection-2",
            RootActorId = "script-runtime-2",
            ScriptId = "script-2",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-2",
                ScriptRevision = "rev-2",
                DefinitionActorId = "definition-2",
                EventType = "AnotherDomainEvent",
                Payload = Any.Pack(new StringValue { Value = "payload" }),
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.StatePayloads.Should().BeEmpty();
        readModel.ReadModelPayloads.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_noop_for_unmapped_events()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "script-projection-3",
            RootActorId = "script-runtime-3",
            ScriptId = "script-3",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new StringValue { Value = "unmapped" }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.StateVersion.Should().Be(0);
        readModel.LastEventType.Should().BeEmpty();
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("script-runtime", TopologyAudience.Self),
    };

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
