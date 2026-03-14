using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
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
    public async Task Promote_ShouldCreateDocument_WhenCatalogEntryDoesNotExist()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
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
                "evt-promote",
                Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-1",
                    Revision = "rev-1",
                    DefinitionActorId = "definition-1",
                    SourceHash = "hash-1",
                    ProposalId = "proposal-1",
                })),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.CatalogActorId.Should().Be("script-catalog");
        document.ScriptId.Should().Be("script-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-1");
        document.LastProposalId.Should().Be("proposal-1");
        document.RevisionHistory.Should().ContainSingle(x => x == "rev-1");
    }

    [Fact]
    public async Task Rollback_ShouldCreateDocument_WhenCatalogEntryDoesNotExist()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
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
                "evt-rollback",
                Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-1",
                    PreviousRevision = "rev-2",
                    ProposalId = "proposal-rollback",
                })),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.CatalogActorId.Should().Be("script-catalog");
        document.ScriptId.Should().Be("script-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.PreviousRevision.Should().Be("rev-2");
        document.LastProposalId.Should().Be("proposal-rollback");
        document.RevisionHistory.Should().ContainSingle(x => x == "rev-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_UnrelatedEvents()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
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
                "evt-other",
                Any.Pack(new ScriptDefinitionUpsertedEvent
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                    SourceText = "source",
                })),
            CancellationToken.None);

        (await dispatcher.ListAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_EventsWithoutScriptId()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
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
                "evt-promote-empty",
                Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "",
                    Revision = "rev-1",
                })),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                "evt-rollback-empty",
                Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "",
                    TargetRevision = "rev-1",
                })),
            CancellationToken.None);

        (await dispatcher.ListAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Promote_ShouldUpdateExistingDocument_WithoutDuplicatingRevisionHistory()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
        await dispatcher.UpsertAsync(new ScriptCatalogEntryDocument
        {
            Id = "script-catalog:script-1",
            CatalogActorId = "script-catalog",
            ScriptId = "script-1",
            ActiveRevision = "rev-1",
            ActiveDefinitionActorId = "definition-1",
            ActiveSourceHash = "hash-1",
            RevisionHistory = { "rev-1" },
            StateVersion = 3,
        }, CancellationToken.None);
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
                "evt-promote-update",
                Any.Pack(new ScriptCatalogRevisionPromotedEvent
                {
                    ScriptId = "script-1",
                    Revision = "rev-1",
                    DefinitionActorId = "definition-2",
                    SourceHash = "hash-2",
                    ProposalId = "proposal-2",
                })),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.PreviousRevision.Should().Be("rev-1");
        document.ActiveRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-2");
        document.ActiveSourceHash.Should().Be("hash-2");
        document.LastProposalId.Should().Be("proposal-2");
        document.StateVersion.Should().Be(4);
        document.RevisionHistory.Should().Equal("rev-1");
    }

    [Fact]
    public async Task Rollback_ShouldPreserveBinding_WhenTargetMatchesCurrentRevision()
    {
        var dispatcher = new InMemoryCatalogEntryStoreDispatcher();
        await dispatcher.UpsertAsync(new ScriptCatalogEntryDocument
        {
            Id = "script-catalog:script-1",
            CatalogActorId = "script-catalog",
            ScriptId = "script-1",
            ActiveRevision = "rev-2",
            PreviousRevision = "rev-1",
            ActiveDefinitionActorId = "definition-2",
            ActiveSourceHash = "hash-2",
            RevisionHistory = { "rev-1", "rev-2" },
            StateVersion = 5,
        }, CancellationToken.None);
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
                "evt-rollback-same",
                Any.Pack(new ScriptCatalogRolledBackEvent
                {
                    ScriptId = "script-1",
                    TargetRevision = "rev-2",
                    PreviousRevision = "rev-1",
                    ProposalId = "proposal-rollback",
                })),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("script-catalog:script-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ActiveRevision.Should().Be("rev-2");
        document.PreviousRevision.Should().Be("rev-1");
        document.ActiveDefinitionActorId.Should().Be("definition-2");
        document.ActiveSourceHash.Should().Be("hash-2");
        document.LastProposalId.Should().Be("proposal-rollback");
        document.StateVersion.Should().Be(6);
        document.RevisionHistory.Should().Equal("rev-1", "rev-2");
    }

    [Fact]
    public void BuildDocumentId_ShouldNormalizeMissingParts()
    {
        ScriptCatalogEntryProjector.BuildDocumentId(null!, null!).Should().Be(":");
    }

    private static EventEnvelope BuildEnvelope(string id, Any payload) =>
        new()
        {
            Id = id,
            Payload = payload,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = id,
            },
        };

    private sealed class InMemoryCatalogEntryStoreDispatcher
        : IProjectionStoreDispatcher<ScriptCatalogEntryDocument, string>
    {
        private readonly Dictionary<string, ScriptCatalogEntryDocument> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptCatalogEntryDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = readModel.DeepClone();
            return Task.CompletedTask;
        }

        public Task MutateAsync(
            string key,
            Action<ScriptCatalogEntryDocument> mutate,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptCatalogEntryDocument { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptCatalogEntryDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel?.DeepClone());
        }

        public Task<IReadOnlyList<ScriptCatalogEntryDocument>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptCatalogEntryDocument>>(
                _store.Values.Take(take).Select(static x => x.DeepClone()).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
