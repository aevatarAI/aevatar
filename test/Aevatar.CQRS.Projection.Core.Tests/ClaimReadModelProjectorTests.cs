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
        var context = CreateContext("claim-runtime-route");

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new StringValue { Value = "ScriptRunDomainEventCommitted" }),
            CancellationToken.None);

        var readModelBefore = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModelBefore.Should().NotBeNull();
        readModelBefore!.StateVersion.Should().Be(0);

        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-route-1",
                ScriptRevision = "rev-route-1",
                DefinitionActorId = "definition-route-1",
                EventType = "ClaimApprovedEvent",
                Payload = Any.Pack(new StringValue { Value = "ClaimApprovedEvent" }),
                ReadModelPayloads =
                {
                    ["claim_case"] = Any.Pack(new Struct
                    {
                        Fields =
                        {
                            ["decision_status"] = Google.Protobuf.WellKnownTypes.Value.ForString("Approved"),
                        },
                    }),
                },
            }),
            CancellationToken.None);

        var readModelAfter = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModelAfter.Should().NotBeNull();
        readModelAfter!.StateVersion.Should().Be(1);
        readModelAfter.LastEventType.Should().Be("ClaimApprovedEvent");
    }

    [Fact]
    public async Task Should_update_decision_status_on_manual_review()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = CreateContext("claim-runtime-manual");

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new ScriptRunDomainEventCommitted
            {
                RunId = "run-manual-1",
                ScriptRevision = "rev-manual-1",
                DefinitionActorId = "definition-manual-1",
                EventType = "ClaimManualReviewRequestedEvent",
                Payload = Any.Pack(new StringValue { Value = "ClaimManualReviewRequestedEvent" }),
                ReadModelPayloads =
                {
                    ["claim_case"] = Any.Pack(new Struct
                    {
                        Fields =
                        {
                            ["decision_status"] = Google.Protobuf.WellKnownTypes.Value.ForString("ManualReview"),
                            ["manual_review_required"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                        },
                    }),
                },
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.LastEventType.Should().Be("ClaimManualReviewRequestedEvent");
        readModel.ReadModelPayloads.Should().ContainKey("claim_case");
        readModel.ReadModelPayloads["claim_case"].Is(Struct.Descriptor).Should().BeTrue();
        var claimCase = readModel.ReadModelPayloads["claim_case"].Unpack<Struct>();
        claimCase.Fields["decision_status"].StringValue.Should().Be("ManualReview");
        claimCase.Fields["manual_review_required"].BoolValue.Should().BeTrue();
    }

    [Fact]
    public async Task Should_noop_for_unmapped_events()
    {
        var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
        var projector = new ScriptExecutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            [new ScriptRunDomainEventCommittedReducer()]);
        var context = CreateContext("claim-runtime-noop");

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            Wrap(new RunScriptRequestedEvent
            {
                RunId = "run-noop-1",
                InputPayload = Any.Pack(new Struct()),
                ScriptRevision = "rev-noop-1",
                DefinitionActorId = "definition-noop-1",
            }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.StateVersion.Should().Be(0);
        readModel.LastEventType.Should().BeEmpty();
    }

    private static ScriptProjectionContext CreateContext(string rootActorId) => new()
    {
        ProjectionId = "claim-projection-" + rootActorId,
        RootActorId = rootActorId,
        ScriptId = "claim-orchestrator",
    };

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
            return Task.FromResult<IReadOnlyList<ScriptExecutionReadModel>>(_store.Values.Take(take).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
