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
            [new ScriptDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "projection-1",
            RootActorId = "script-host-1",
            ScriptId = "script-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptDomainEventCommitted
            {
                RunId = "run-1",
                ScriptId = "script-1",
                ScriptRevision = "rev-1",
                EventType = "script.run.completed",
                PayloadJson = "{\"ok\":true}",
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("script-host-1", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ScriptId.Should().Be("script-1");
        readModel.Revision.Should().Be("rev-1");
        readModel.LastRunId.Should().Be("run-1");
        readModel.LastEventType.Should().Be("script.run.completed");
        readModel.StatePayloadJson.Should().Be("{\"ok\":true}");
        readModel.StateVersion.Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnmappedEventType()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptDomainEventCommittedReducer()]);
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
                InputJson = "{}",
                ScriptRevision = "rev-2",
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
        PublisherId = "script-host",
        Direction = EventDirection.Self,
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
