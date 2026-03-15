using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptCatalogEntryProjectorTests
{
    [Fact]
    public async Task Promote_ShouldCreateDocument_FromCommittedCatalogState()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote",
                version: 1,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-1",
                    Revision = "rev-1",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 1,
                    scriptId: "script-1",
                    activeRevision: "rev-1",
                    activeDefinitionActorId: "definition-1",
                    activeSourceHash: "hash-1",
                    previousRevision: string.Empty,
                    lastProposalId: "proposal-1",
                    revisionHistory: ["rev-1"])),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.CatalogActorId.Should().Be("script-catalog");
        document.ScriptId.Should().Be("script-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-1");
        document.ActiveSourceHash.Should().Be("hash-1");
        document.LastProposalId.Should().Be("proposal-1");
        document.StateVersion.Should().Be(1);
        document.LastEventId.Should().Be("evt-promote");
        document.RevisionHistory.Should().ContainSingle(x => x == "rev-1");
    }

    [Fact]
    public async Task Rollback_ShouldCreateDocument_FromCommittedCatalogState()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-rollback",
                version: 2,
                payload: Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-1",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 2,
                    scriptId: "script-1",
                    activeRevision: "rev-1",
                    activeDefinitionActorId: string.Empty,
                    activeSourceHash: string.Empty,
                    previousRevision: "rev-2",
                    lastProposalId: "proposal-rollback",
                    revisionHistory: ["rev-1"])),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.CatalogActorId.Should().Be("script-catalog");
        document.ScriptId.Should().Be("script-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.PreviousRevision.Should().Be("rev-2");
        document.LastProposalId.Should().Be("proposal-rollback");
        document.StateVersion.Should().Be(2);
        document.LastEventId.Should().Be("evt-rollback");
        document.RevisionHistory.Should().ContainSingle(x => x == "rev-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_UnrelatedCommittedEvents()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-other",
                version: 1,
                payload: Any.Pack(new ScriptDefinitionUpsertedEvent
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                    SourceText = "source",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 1,
                    scriptId: "script-1",
                    activeRevision: "rev-1",
                    activeDefinitionActorId: "definition-1",
                    activeSourceHash: "hash-1",
                    previousRevision: string.Empty,
                    lastProposalId: "proposal-1",
                    revisionHistory: ["rev-1"])),
            CancellationToken.None);

        (await dispatcher.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 10,
            },
            CancellationToken.None)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_EventsWithoutScriptId()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote-empty",
                version: 1,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "",
                    Revision = "rev-1",
                }),
                state: new ScriptCatalogState()),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-rollback-empty",
                version: 2,
                payload: Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "",
                    TargetRevision = "rev-1",
                }),
                state: new ScriptCatalogState()),
            CancellationToken.None);

        (await dispatcher.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 10,
            },
            CancellationToken.None)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Promote_ShouldUpdateDocument_UsingCommittedCatalogEntryState()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote-update",
                version: 4,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-1",
                    Revision = "rev-1",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 4,
                    scriptId: "script-1",
                    activeRevision: "rev-1",
                    activeDefinitionActorId: "definition-2",
                    activeSourceHash: "hash-2",
                    previousRevision: "rev-1",
                    lastProposalId: "proposal-2",
                    revisionHistory: ["rev-1"])),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.PreviousRevision.Should().Be("rev-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-2");
        document.ActiveSourceHash.Should().Be("hash-2");
        document.LastProposalId.Should().Be("proposal-2");
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("evt-promote-update");
        document.RevisionHistory.Should().Equal("rev-1");
    }

    [Fact]
    public async Task Rollback_ShouldPreserveBinding_WhenCatalogStateStillPointsToSameRevision()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-rollback-same",
                version: 6,
                payload: Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-2",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 6,
                    scriptId: "script-1",
                    activeRevision: "rev-2",
                    activeDefinitionActorId: "definition-2",
                    activeSourceHash: "hash-2",
                    previousRevision: "rev-1",
                    lastProposalId: "proposal-rollback",
                    revisionHistory: ["rev-1", "rev-2"])),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ActiveRevision.Should().Be("rev-2");
        document.PreviousRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-2");
        document.ActiveSourceHash.Should().Be("hash-2");
        document.LastProposalId.Should().Be("proposal-rollback");
        document.StateVersion.Should().Be(6);
        document.LastEventId.Should().Be("evt-rollback-same");
        document.RevisionHistory.Should().Equal("rev-1", "rev-2");
    }

    [Fact]
    public async Task RollbackRequested_ShouldAdvanceCatalogDocumentVersion_FromCommittedState()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote-initial",
                version: 2,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-1",
                    Revision = "rev-2",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 2,
                    scriptId: "script-1",
                    activeRevision: "rev-2",
                    activeDefinitionActorId: "definition-2",
                    activeSourceHash: "hash-2",
                    previousRevision: "rev-1",
                    lastProposalId: "proposal-promote",
                    revisionHistory: ["rev-1", "rev-2"])),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-rollback-requested",
                version: 3,
                payload: Any.Pack(new ScriptCatalogRollbackRequestedEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-1",
                    ProposalId = "proposal-rollback",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 3,
                    scriptId: "script-1",
                    activeRevision: "rev-2",
                    activeDefinitionActorId: "definition-2",
                    activeSourceHash: "hash-2",
                    previousRevision: "rev-1",
                    lastProposalId: "proposal-promote",
                    revisionHistory: ["rev-1", "rev-2"])),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-rollback-completed",
                version: 4,
                payload: Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-1",
                    ProposalId = "proposal-rollback",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 4,
                    scriptId: "script-1",
                    activeRevision: "rev-1",
                    activeDefinitionActorId: string.Empty,
                    activeSourceHash: string.Empty,
                    previousRevision: "rev-2",
                    lastProposalId: "proposal-rollback",
                    revisionHistory: ["rev-1", "rev-2"])),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ActiveRevision.Should().Be("rev-1");
        document.LastProposalId.Should().Be("proposal-rollback");
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("evt-rollback-completed");
    }

    [Fact]
    public async Task Promote_ForAnotherScript_ShouldStillAdvanceExistingEntryVersion_FromCommittedState()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptCatalogEntryDocument>();
        var projector = new ScriptCatalogEntryProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            ProjectionId = "script-catalog:authority",
            RootActorId = "script-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote-a2",
                version: 1,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-a",
                    Revision = "rev-a-2",
                }),
                state: BuildCatalogState(
                    lastAppliedEventVersion: 1,
                    scriptId: "script-a",
                    activeRevision: "rev-a-2",
                    activeDefinitionActorId: "definition-a-2",
                    activeSourceHash: "hash-a-2",
                    previousRevision: "rev-a-1",
                    lastProposalId: "proposal-a-2",
                    revisionHistory: ["rev-a-1", "rev-a-2"])),
            CancellationToken.None);

        var nextState = new ScriptCatalogState
        {
            LastAppliedEventVersion = 2,
            LastEventId = "evt-promote-b2",
            Entries =
            {
                ["script-a"] = new ScriptCatalogEntryState
                {
                    ScriptId = "script-a",
                    ActiveRevision = "rev-a-2",
                    ActiveDefinitionActorId = "definition-a-2",
                    ActiveSourceHash = "hash-a-2",
                    PreviousRevision = "rev-a-1",
                    LastProposalId = "proposal-a-2",
                    RevisionHistory = { "rev-a-1", "rev-a-2" },
                },
                ["script-b"] = new ScriptCatalogEntryState
                {
                    ScriptId = "script-b",
                    ActiveRevision = "rev-b-2",
                    ActiveDefinitionActorId = "definition-b-2",
                    ActiveSourceHash = "hash-b-2",
                    PreviousRevision = "rev-b-1",
                    LastProposalId = "proposal-b-2",
                    RevisionHistory = { "rev-b-1", "rev-b-2" },
                },
            },
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                eventId: "evt-promote-b2",
                version: 2,
                payload: Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-b",
                    Revision = "rev-b-2",
                }),
                state: nextState),
            CancellationToken.None);

        var documentA = await dispatcher.GetAsync("script-catalog:script-a", CancellationToken.None);
        var documentB = await dispatcher.GetAsync("script-catalog:script-b", CancellationToken.None);
        documentA.Should().NotBeNull();
        documentB.Should().NotBeNull();
        documentA!.StateVersion.Should().Be(2);
        documentA.LastEventId.Should().Be("evt-promote-b2");
        documentA.ActiveRevision.Should().Be("rev-a-2");
        documentB!.StateVersion.Should().Be(2);
        documentB.ActiveRevision.Should().Be("rev-b-2");
    }

    [Fact]
    public void BuildDocumentId_ShouldNormalizeMissingParts()
    {
        ScriptCatalogEntryProjector.BuildDocumentId(null!, null!).Should().Be(":");
    }

    private static EventEnvelope BuildEnvelope(
        string eventId,
        long version,
        Any payload,
        ScriptCatalogState state)
    {
        var occurredAt = Timestamp.FromDateTime(DateTime.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = payload.Clone(),
                },
                StateRoot = Any.Pack(state),
            }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = eventId,
            },
        };
    }

    private static ScriptCatalogState BuildCatalogState(
        long lastAppliedEventVersion,
        string scriptId,
        string activeRevision,
        string activeDefinitionActorId,
        string activeSourceHash,
        string previousRevision,
        string lastProposalId,
        IEnumerable<string> revisionHistory)
    {
        var state = new ScriptCatalogState
        {
            LastAppliedEventVersion = lastAppliedEventVersion,
            LastEventId = $"evt-{lastAppliedEventVersion}",
        };
        state.Entries[scriptId] = new ScriptCatalogEntryState
        {
            ScriptId = scriptId,
            ActiveRevision = activeRevision,
            ActiveDefinitionActorId = activeDefinitionActorId,
            ActiveSourceHash = activeSourceHash,
            PreviousRevision = previousRevision,
            LastProposalId = lastProposalId,
        };
        state.Entries[scriptId].RevisionHistory.Add(revisionHistory);
        return state;
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
