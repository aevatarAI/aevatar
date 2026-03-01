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

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ClaimReadModelProjectorTests
{
    [Fact]
    public async Task Should_route_by_exact_type_url()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "claim-projection-1",
            RootActorId = "claim-runtime-1",
            ScriptId = "claim-script-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-claim-1",
                ScriptRevision = "rev-claim-1",
                DefinitionActorId = "definition-1",
                EventType = "ClaimManualReviewRequestedEvent",
                PayloadJson = "{\"caseId\":\"Case-B\"}",
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.LastEventType.Should().Be("ClaimManualReviewRequestedEvent");
        readModel.DecisionStatus.Should().Be("ManualReview");
        readModel.ManualReviewRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Should_update_decision_status_on_manual_review()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = new ScriptProjectionContext
        {
            ProjectionId = "claim-projection-2",
            RootActorId = "claim-runtime-2",
            ScriptId = "claim-script-2",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-claim-2",
                ScriptRevision = "rev-claim-2",
                DefinitionActorId = "definition-2",
                EventType = "ClaimApprovedEvent",
                PayloadJson = "{\"caseId\":\"Case-A\"}",
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.DecisionStatus.Should().Be("Approved");
        readModel.ManualReviewRequired.Should().BeFalse();
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
            ProjectionId = "claim-projection-3",
            RootActorId = "claim-runtime-3",
            ScriptId = "claim-script-3",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new StringValue { Value = "unmapped" }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.StateVersion.Should().Be(0);
        readModel.DecisionStatus.Should().BeEmpty();
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = "claim-runtime",
        Direction = EventDirection.Self,
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
