using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Application;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ScriptGAgentEndToEndTests
{
    [Fact]
    public async Task Run_ShouldFlow_CommandToEnvelope_ToActor_ToProjection()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        var actor = await runtime.CreateAsync<ScriptHostGAgent>("script-host-" + Guid.NewGuid().ToString("N")[..8]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "script-projection-" + Guid.NewGuid().ToString("N")[..8],
            RootActorId = actor.Id,
            ScriptId = "script-1",
        };

        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptDomainEventCommittedReducer()]);
        await projector.InitializeAsync(context, CancellationToken.None);

        var adapter = new RunScriptCommandAdapter();
        var envelope = adapter.Map(
            new RunScriptCommand(
                RunId: "run-1",
                InputJson: "{\"name\":\"alice\"}",
                ScriptRevision: "rev-1"),
            actor.Id);
        await actor.HandleEventAsync(envelope, CancellationToken.None);

        var persisted = await eventStore.GetEventsAsync(actor.Id, ct: CancellationToken.None);
        var committedStateEvent = persisted
            .LastOrDefault(x => x.EventData?.Is(ScriptDomainEventCommitted.Descriptor) == true);
        committedStateEvent.Should().NotBeNull();

        var projectionEnvelope = new EventEnvelope
        {
            Id = committedStateEvent!.EventId,
            Timestamp = committedStateEvent.Timestamp,
            Payload = committedStateEvent.EventData,
            PublisherId = actor.Id,
            Direction = EventDirection.Self,
            CorrelationId = "run-1",
        };
        await projector.ProjectAsync(context, projectionEnvelope, CancellationToken.None);
        var committedEvent = committedStateEvent.EventData.Unpack<ScriptDomainEventCommitted>();
        var readModel = await dispatcher.GetAsync(actor.Id, CancellationToken.None);

        committedEvent.RunId.Should().Be("run-1");
        committedEvent.ScriptRevision.Should().Be("rev-1");
        committedEvent.EventType.Should().Be("script.run.completed");
        committedEvent.PayloadJson.Should().Contain("result");

        readModel.Should().NotBeNull();
        readModel!.Id.Should().Be(actor.Id);
        readModel.ScriptId.Should().Be("script-1");
        readModel.Revision.Should().Be("rev-1");
        readModel.LastRunId.Should().Be("run-1");
        readModel.LastEventType.Should().Be("script.run.completed");
        readModel.StatePayloadJson.Should().Contain("result");
        readModel.StateVersion.Should().Be(1);

        await runtime.DestroyAsync(actor.Id, CancellationToken.None);
    }

    private sealed class InMemoryScriptProjectionStoreDispatcher
        : IProjectionStoreDispatcher<ScriptExecutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptExecutionReadModel> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(
            ScriptExecutionReadModel readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(
            string key,
            Action<ScriptExecutionReadModel> mutate,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptExecutionReadModel { Id = key };
                _items[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptExecutionReadModel?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptExecutionReadModel>> ListAsync(
            int take = 50,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptExecutionReadModel>>(
                _items.Values.Take(take).ToList());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
