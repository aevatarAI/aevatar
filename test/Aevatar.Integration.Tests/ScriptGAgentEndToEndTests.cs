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

public class ScriptGAgentEndToEndTests
{
    [Fact]
    public async Task Run_ShouldFlow_DefinitionAndRuntimeEvents_ToProjection()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(
            "script-definition-" + Guid.NewGuid().ToString("N")[..8]);
        var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(
            "script-runtime-" + Guid.NewGuid().ToString("N")[..8]);

        var upsertAdapter = new UpsertScriptDefinitionCommandAdapter();
        var definitionEnvelope = upsertAdapter.Map(
            new UpsertScriptDefinitionCommand(
                ScriptId: "script-1",
                ScriptRevision: "rev-1",
                SourceText: "return 1;",
                SourceHash: "hash-1"),
            definitionActor.Id);
        await definitionActor.HandleEventAsync(definitionEnvelope, CancellationToken.None);

        var context = new ScriptProjectionContext
        {
            ProjectionId = "script-projection-" + Guid.NewGuid().ToString("N")[..8],
            RootActorId = runtimeActor.Id,
            ScriptId = "script-1",
        };
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        await projector.InitializeAsync(context, CancellationToken.None);

        var runAdapter = new RunScriptCommandAdapter();
        var runEnvelope = runAdapter.Map(
            new RunScriptCommand(
                RunId: "run-1",
                InputJson: "{\"name\":\"alice\"}",
                ScriptRevision: "rev-1",
                DefinitionActorId: definitionActor.Id),
            runtimeActor.Id);
        await runtimeActor.HandleEventAsync(runEnvelope, CancellationToken.None);

        var persisted = await eventStore.GetEventsAsync(runtimeActor.Id, ct: CancellationToken.None);
        var committedStateEvent = persisted
            .LastOrDefault(x => x.EventData?.Is(ScriptRunDomainEventCommitted.Descriptor) == true);
        committedStateEvent.Should().NotBeNull();

        var projectionEnvelope = new EventEnvelope
        {
            Id = committedStateEvent!.EventId,
            Timestamp = committedStateEvent.Timestamp,
            Payload = committedStateEvent.EventData,
            PublisherId = runtimeActor.Id,
            Direction = EventDirection.Self,
            CorrelationId = "run-1",
        };
        await projector.ProjectAsync(context, projectionEnvelope, CancellationToken.None);

        var committedEvent = committedStateEvent.EventData.Unpack<ScriptRunDomainEventCommitted>();
        var readModel = await dispatcher.GetAsync(runtimeActor.Id, CancellationToken.None);

        committedEvent.RunId.Should().Be("run-1");
        committedEvent.ScriptRevision.Should().Be("rev-1");
        committedEvent.DefinitionActorId.Should().Be(definitionActor.Id);
        committedEvent.EventType.Should().Be("script.run.completed");
        committedEvent.PayloadJson.Should().Contain("result");

        readModel.Should().NotBeNull();
        readModel!.Id.Should().Be(runtimeActor.Id);
        readModel.ScriptId.Should().Be("script-1");
        readModel.DefinitionActorId.Should().Be(definitionActor.Id);
        readModel.Revision.Should().Be("rev-1");
        readModel.LastRunId.Should().Be("run-1");
        readModel.LastEventType.Should().Be("script.run.completed");
        readModel.StatePayloadJson.Should().Contain("result");
        readModel.StateVersion.Should().Be(1);

        await runtime.DestroyAsync(definitionActor.Id, CancellationToken.None);
        await runtime.DestroyAsync(runtimeActor.Id, CancellationToken.None);
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
