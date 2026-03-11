using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ScriptExecutionReadModelProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldRouteByExactTypeUrl()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "projection-1",
            RootActorId = "script-host-1",
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
                ReadModelSchemaVersion = "7",
                EventType = "script.run.completed",
                Payload = Any.Pack(new StringValue { Value = "domain-ok" }),
                StatePayloads = { ["state"] = Any.Pack(new Int32Value { Value = 42 }) },
                ReadModelPayloads = { ["view"] = Any.Pack(new StringValue { Value = "view-ok" }) },
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("script-host-1", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ScriptId.Should().Be("script-1");
        readModel.DefinitionActorId.Should().Be("definition-1");
        readModel.Revision.Should().Be("rev-1");
        readModel.ReadModelSchemaVersion.Should().Be("7");
        readModel.LastRunId.Should().Be("run-1");
        readModel.LastEventType.Should().Be("script.run.completed");
        readModel.LastDomainEventPayload.Should().NotBeNull();
        readModel.LastDomainEventPayload!.Unpack<StringValue>().Value.Should().Be("domain-ok");
        readModel.StatePayloads.Should().ContainKey("state");
        readModel.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(42);
        readModel.ReadModelPayloads.Should().ContainKey("view");
        readModel.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("view-ok");
        readModel.StateVersion.Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnmappedEventType()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "projection-2",
            RootActorId = "script-host-2",
            ScriptId = "script-2",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new RunScriptRequestedEvent
            {
                RunId = "run-2",
                InputPayload = Any.Pack(new Struct()),
                ScriptRevision = "rev-2",
                DefinitionActorId = "definition-2",
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("script-host-2", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ScriptId.Should().Be("script-2");
        readModel.Revision.Should().BeEmpty();
        readModel.StateVersion.Should().Be(0);
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = new EnvelopeRoute
        {
            PublisherActorId = "script-host",
            Direction = EventDirection.Self,
        },
    };

    private sealed class InMemoryScriptProjectionStoreDispatcher
        : IProjectionStoreDispatcher<ScriptExecutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptExecutionReadModel> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(
            ScriptExecutionReadModel readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(
            string key,
            Action<ScriptExecutionReadModel> mutate,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptExecutionReadModel { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptExecutionReadModel?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptExecutionReadModel>> ListAsync(
            int take = 50,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptExecutionReadModel>>(_store.Values.Take(take).ToList());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
