using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Abstractions.Definitions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ScriptEvolutionReadModelProjectorTests
{
    [Fact]
    public async Task Should_Project_Proposed_Validated_And_Promoted_Timeline()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        var projector = new ScriptEvolutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = "projection-evolution-1",
            RootActorId = "evolution-manager-1",
            ProposalId = "proposal-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-started",
                1,
                new ScriptEvolutionSessionStartedEvent
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    Status = "session_started",
                    LastAppliedEventVersion = 1,
                    LastEventId = "proposal-1:session-started",
                }),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-validated",
                2,
                new ScriptEvolutionValidatedEvent
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    CandidateRevision = "rev-2",
                    IsValid = true,
                    Diagnostics = { "compile-ok" },
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    Status = ScriptEvolutionStatuses.Validated,
                    ValidationSucceeded = true,
                    Diagnostics = { "compile-ok" },
                    LastAppliedEventVersion = 2,
                    LastEventId = "proposal-1:validated",
                }),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-promoted",
                3,
                new ScriptEvolutionSessionCompletedEvent
                {
                    ProposalId = "proposal-1",
                    Accepted = true,
                    Status = ScriptEvolutionStatuses.Promoted,
                    DefinitionActorId = "definition-1",
                    CatalogActorId = "catalog-1",
                    Diagnostics = { "compile-ok" },
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-1",
                    ScriptId = "script-1",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    Status = ScriptEvolutionStatuses.Promoted,
                    Completed = true,
                    Accepted = true,
                    ValidationSucceeded = true,
                    DefinitionActorId = "definition-1",
                    CatalogActorId = "catalog-1",
                    Diagnostics = { "compile-ok" },
                    LastAppliedEventVersion = 3,
                    LastEventId = "proposal-1:session-completed",
                }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("proposal-1", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ProposalId.Should().Be("proposal-1");
        readModel.ScriptId.Should().Be("script-1");
        readModel.CandidateRevision.Should().Be("rev-2");
        readModel.ValidationStatus.Should().Be("validated");
        readModel.PromotionStatus.Should().Be("promoted");
        readModel.StateVersion.Should().Be(3);
        readModel.LastEventId.Should().Be("evt-promoted");
        readModel.Diagnostics.Should().ContainSingle(x => x == "compile-ok");
        readModel.DefinitionActorId.Should().Be("definition-1");
        readModel.CatalogActorId.Should().Be("catalog-1");
    }

    [Fact]
    public async Task Should_Project_Rejected_And_RolledBack_Statuses()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        var projector = new ScriptEvolutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = "projection-evolution-2",
            RootActorId = "evolution-manager-2",
            ProposalId = "proposal-2",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-proposed-2",
                1,
                new ScriptEvolutionProposedEvent
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    CandidateSourceHash = "hash-rev-2",
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    Status = ScriptEvolutionStatuses.Proposed,
                    LastAppliedEventVersion = 1,
                    LastEventId = "proposal-2:proposed",
                }),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rejected-2",
                2,
                new ScriptEvolutionRejectedEvent
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    CandidateRevision = "rev-2",
                    FailureReason = "policy-denied",
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-2",
                    Status = ScriptEvolutionStatuses.Rejected,
                    FailureReason = "policy-denied",
                    LastAppliedEventVersion = 2,
                    LastEventId = "proposal-2:rejected",
                }),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rolled-back-2",
                3,
                new ScriptEvolutionSessionCompletedEvent
                {
                    ProposalId = "proposal-2",
                    Accepted = false,
                    Status = ScriptEvolutionStatuses.RolledBack,
                    CatalogActorId = "catalog-2",
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-2",
                    ScriptId = "script-2",
                    BaseRevision = "rev-1",
                    CandidateRevision = "rev-1",
                    Status = ScriptEvolutionStatuses.RolledBack,
                    Completed = true,
                    Accepted = false,
                    CatalogActorId = "catalog-2",
                    LastAppliedEventVersion = 3,
                    LastEventId = "proposal-2:session-completed",
                }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("proposal-2", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.ProposalId.Should().Be("proposal-2");
        readModel.PromotionStatus.Should().Be("rolled_back");
        readModel.RollbackStatus.Should().Be("rolled_back");
        readModel.CandidateRevision.Should().Be("rev-1");
        readModel.StateVersion.Should().Be(3);
        readModel.LastEventId.Should().Be("evt-rolled-back-2");
    }

    [Fact]
    public async Task Should_Expose_Intermediate_Rollback_Requested_State()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        var projector = new ScriptEvolutionReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = "projection-evolution-3",
            RootActorId = "evolution-manager-3",
            ProposalId = "proposal-3",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rollback-requested-3",
                1,
                new ScriptEvolutionRollbackRequestedEvent
                {
                    ProposalId = "proposal-3",
                    ScriptId = "script-3",
                    TargetRevision = "rev-1",
                    Reason = "manual-revert",
                    CatalogActorId = "catalog-3",
                },
                new ScriptEvolutionSessionState
                {
                    ProposalId = "proposal-3",
                    ScriptId = "script-3",
                    BaseRevision = "rev-0",
                    CandidateRevision = "rev-1",
                    Status = ScriptEvolutionStatuses.RollbackRequested,
                    FailureReason = "manual-revert",
                    CatalogActorId = "catalog-3",
                    LastAppliedEventVersion = 1,
                    LastEventId = "proposal-3:rollback_requested",
                }),
            CancellationToken.None);

        var readModel = await dispatcher.GetAsync("proposal-3", CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.PromotionStatus.Should().Be(ScriptEvolutionStatuses.RollbackRequested);
        readModel.RollbackStatus.Should().Be(ScriptEvolutionStatuses.RollbackRequested);
        readModel.FailureReason.Should().Be("manual-revert");
    }

    private static EventEnvelope BuildEnvelope(
        string id,
        long version,
        IMessage payload,
        ScriptEvolutionSessionState state)
    {
        var timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = id,
            Timestamp = timestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = id,
                    Version = version,
                    Timestamp = timestamp,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = id,
            },
        };
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
