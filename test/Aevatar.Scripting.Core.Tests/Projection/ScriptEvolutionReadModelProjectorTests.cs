using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ScriptEvolutionReadModelProjectorTests
{
    [Fact]
    public async Task Should_Project_Proposed_Validated_And_Promoted_Timeline()
    {
        var dispatcher = new InMemoryEvolutionProjectionStoreDispatcher();
        var projector = new ScriptEvolutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            [
                new ScriptEvolutionProposedEventReducer(),
                new ScriptEvolutionValidatedEventReducer(),
                new ScriptEvolutionPromotedEventReducer(),
            ]);

        var context = new ScriptEvolutionProjectionContext
        {
            ProjectionId = "projection-evolution-1",
            RootActorId = "evolution-manager-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-proposed",
                Any.Pack(new ScriptEvolutionProposedEvent
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    CandidateSourceHash = "hash-rev-2",
                })),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-validated",
                Any.Pack(new ScriptEvolutionValidatedEvent
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    CandidateRevision = "rev-2",
                    IsValid = true,
                    Diagnostics = { "compile-ok" },
                })),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-promoted",
                Any.Pack(new ScriptEvolutionPromotedEvent
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    CandidateRevision = "rev-2",
                    DefinitionActorId = "definition-1",
                    CatalogActorId = "catalog-1",
                })),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("proposal-1", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ProposalId.Should().Be("proposal-1");
        readModel.ScriptId.Should().Be("script-1");
        readModel.CandidateRevision.Should().Be("rev-2");
        readModel.ValidationStatus.Should().Be("validated");
        readModel.PromotionStatus.Should().Be("promoted");
        readModel.Diagnostics.Should().ContainSingle(x => x == "compile-ok");
        readModel.DefinitionActorId.Should().Be("definition-1");
        readModel.CatalogActorId.Should().Be("catalog-1");
    }

    [Fact]
    public async Task Should_Project_Rejected_And_RolledBack_Statuses()
    {
        var dispatcher = new InMemoryEvolutionProjectionStoreDispatcher();
        var projector = new ScriptEvolutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            [
                new ScriptEvolutionProposedEventReducer(),
                new ScriptEvolutionRejectedEventReducer(),
                new ScriptEvolutionRolledBackEventReducer(),
            ]);

        var context = new ScriptEvolutionProjectionContext
        {
            ProjectionId = "projection-evolution-2",
            RootActorId = "evolution-manager-2",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-proposed-2",
                Any.Pack(new ScriptEvolutionProposedEvent
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    CandidateSourceHash = "hash-rev-2",
                })),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rejected-2",
                Any.Pack(new ScriptEvolutionRejectedEvent
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    CandidateRevision = "rev-2",
                    FailureReason = "policy-denied",
                })),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rolled-back-2",
                Any.Pack(new ScriptEvolutionRolledBackEvent
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    TargetRevision = "rev-1",
                    PreviousRevision = "rev-2",
                    CatalogActorId = "catalog-2",
                })),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("proposal-2", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ProposalId.Should().Be("proposal-2");
        readModel.PromotionStatus.Should().Be("rolled_back");
        readModel.RollbackStatus.Should().Be("rolled_back");
        readModel.CandidateRevision.Should().Be("rev-1");
        readModel.FailureReason.Should().Be("policy-denied");
    }

    private static EventEnvelope BuildEnvelope(string id, Any payload)
    {
        return new EventEnvelope
        {
            Id = id,
            Payload = payload,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = "projection-test",
            Direction = EventDirection.Self,
            CorrelationId = id,
        };
    }

    private sealed class InMemoryEvolutionProjectionStoreDispatcher
        : IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptEvolutionReadModel> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptEvolutionReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(
            string key,
            Action<ScriptEvolutionReadModel> mutate,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptEvolutionReadModel { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptEvolutionReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptEvolutionReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptEvolutionReadModel>>(
                _store.Values.Take(take).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
