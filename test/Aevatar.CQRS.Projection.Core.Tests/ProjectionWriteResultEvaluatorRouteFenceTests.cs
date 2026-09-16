using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Pure evaluator cases for the route-epoch fence of <see cref="ProjectionWriteResultEvaluator"/>
/// on <see cref="IProjectionRouteFencedReadModel"/> documents (#3476), with
/// <see cref="ProjectionScopeStatusDocument"/> as the real fenced read model:
/// <list type="bullet">
/// <item>same source version, strictly higher route epoch: Applied (same-version takeover; the
/// fence is a writer-authority statement and precedes the event-id and byte rules);</item>
/// <item>same source version, strictly lower route epoch: Stale (never Conflict);</item>
/// <item>same source version, equal epoch: the plain identity rules — event ids must match (else
/// Conflict) and bytes must be identical (Duplicate) else Conflict;</item>
/// <item>across source versions the fence never applies: a higher version is Applied whatever its
/// epoch (an epoch-0 old binary can take a document forward), a lower version is Stale;</item>
/// <item>a document without a route has epoch 0, so two route-less documents (B0/B2 shapes) are
/// decided exactly as the old plain evaluator decided them;</item>
/// <item>non-fenced read models are untouched: every decision equals the old plain evaluator's.</item>
/// </list>
/// The old plain evaluator is <see cref="PreviousBinaryProjectionWriteResultEvaluator"/>, the
/// verbatim evaluator of b64c96a45 / 8d47b5e5c / 416e80f4a.
/// </summary>
public sealed class ProjectionWriteResultEvaluatorRouteFenceTests
{
    private const string ActorId = "scope:root-actor:kind";
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    // ── same version, equal epoch ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SameVersion_EqualEpoch_IdenticalBytes_IsDuplicate_BothDirections(long epoch)
    {
        var first = Document(version: 5, "evt-5", epoch);
        var second = Document(version: 5, "evt-5", epoch);

        Evaluate(first, second).Should().Be(ProjectionWriteDisposition.Duplicate);
        Evaluate(second, first).Should().Be(ProjectionWriteDisposition.Duplicate);
        Plain(first, second).Should().Be(ProjectionWriteDisposition.Duplicate, "identical bytes were always a duplicate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SameVersion_EqualEpoch_DifferentEventId_IsConflict(long epoch)
    {
        var existing = Document(version: 5, "evt-5", epoch);
        var incoming = Document(version: 5, "evt-5-other-fact", epoch);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict);
        Plain(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SameVersion_EqualEpoch_SameEvent_DifferentBytes_IsConflict(long epoch)
    {
        var existing = Document(version: 5, "evt-5", epoch, highestSeenVersion: 50);
        var incoming = Document(version: 5, "evt-5", epoch, highestSeenVersion: 51);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict);
        Plain(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict);
    }

    [Fact]
    public void SameVersion_EqualEpoch_SameEvent_DifferentRoutePhaseOnly_IsConflict()
    {
        // Same epoch, same version, but one writer mapped the route while BLOCKED and another while
        // ACTIVE: that cannot happen for one committed source version (the state at a version has
        // one route), and if it did the bytes differ, so it is an honest conflict, not a duplicate.
        var existing = Document(version: 5, "evt-5", epoch: 1, phase: ProjectionScopeStatusRoutePhase.Blocked);
        var incoming = Document(version: 5, "evt-5", epoch: 1, phase: ProjectionScopeStatusRoutePhase.Active);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict);
    }

    // ── same version, different epoch: the fence ────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(0, 2)]
    public void SameVersion_HigherEpoch_TakesOver_Applied(long existingEpoch, long incomingEpoch)
    {
        var existing = Document(version: 5, "evt-5", existingEpoch);
        var incoming = Document(version: 5, "evt-5", incomingEpoch);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Applied);
        Plain(existing, incoming).Should().Be(ProjectionWriteDisposition.Conflict, "the old plain evaluator only sees different bytes at one version");
    }

    [Fact]
    public void SameVersion_HigherEpoch_TakesOver_EvenWithDifferentEventIdOrBytes()
    {
        // The fence precedes the identity rules: a strictly higher epoch is the newer writer
        // authority for this version. One committed source version has one event id, so a
        // different event id here can only come from a rewritten source history — the higher
        // epoch wins there too.
        var existing = Document(version: 5, "evt-5", epoch: 1, highestSeenVersion: 50);

        Evaluate(existing, Document(version: 5, "evt-5-other-fact", epoch: 2)).Should().Be(ProjectionWriteDisposition.Applied);
        Evaluate(existing, Document(version: 5, "evt-5", epoch: 2, highestSeenVersion: 51)).Should().Be(ProjectionWriteDisposition.Applied);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 0)]
    public void SameVersion_LowerEpoch_IsStale_NeverConflict(long existingEpoch, long incomingEpoch)
    {
        var existing = Document(version: 5, "evt-5", existingEpoch);

        Evaluate(existing, Document(version: 5, "evt-5", incomingEpoch)).Should().Be(ProjectionWriteDisposition.Stale);
        Evaluate(existing, Document(version: 5, "evt-5-other-fact", incomingEpoch)).Should().Be(ProjectionWriteDisposition.Stale, "the fence precedes the event-id rule");
        Evaluate(existing, Document(version: 5, "evt-5", incomingEpoch, highestSeenVersion: 51)).Should().Be(ProjectionWriteDisposition.Stale, "the fence precedes the byte rule");
        Plain(existing, Document(version: 5, "evt-5", incomingEpoch)).Should().Be(ProjectionWriteDisposition.Conflict, "what the old binary logs for its own refused write");
    }

    // ── across versions: version wins ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void HigherVersion_IsApplied_WhateverTheEpoch(long existingEpoch, long incomingEpoch)
    {
        var existing = Document(version: 5, "evt-5", existingEpoch);
        var incoming = Document(version: 6, "evt-6", incomingEpoch);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Applied, "an old binary after rollback takes the document forward");
        Plain(existing, incoming).Should().Be(ProjectionWriteDisposition.Applied);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 0)]
    public void LowerVersion_IsStale_WhateverTheEpoch(long existingEpoch, long incomingEpoch)
    {
        var existing = Document(version: 6, "evt-6", existingEpoch);
        var incoming = Document(version: 5, "evt-5", incomingEpoch);

        Evaluate(existing, incoming).Should().Be(ProjectionWriteDisposition.Stale);
        Plain(existing, incoming).Should().Be(ProjectionWriteDisposition.Stale);
    }

    // ── the fence through a real store ──────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_AppliesTheFence_TakeoverThenDuplicateThenStale_ThenVersionWins()
    {
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static d => d.Id);

        (await store.UpsertAsync(Document(5, "evt-5", epoch: 0))).Disposition.Should().Be(ProjectionWriteDisposition.Applied, "B0/B2 wrote v5");
        (await store.UpsertAsync(Document(5, "evt-5", epoch: 1))).Disposition.Should().Be(ProjectionWriteDisposition.Applied, "epoch-1 takeover");
        (await store.UpsertAsync(Document(5, "evt-5", epoch: 1))).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate, "repeat of the takeover");
        (await store.UpsertAsync(Document(5, "evt-5", epoch: 0))).Disposition.Should().Be(ProjectionWriteDisposition.Stale, "late epoch-0 write");
        (await store.UpsertAsync(Document(5, "evt-5", epoch: 2))).Disposition.Should().Be(ProjectionWriteDisposition.Applied, "re-adoption at epoch 2");
        (await store.UpsertAsync(Document(5, "evt-5", epoch: 1))).Disposition.Should().Be(ProjectionWriteDisposition.Stale, "late epoch-1 write");
        (await store.UpsertAsync(Document(5, "evt-5-other-fact", epoch: 2))).Disposition.Should().Be(ProjectionWriteDisposition.Conflict, "same epoch, different fact");
        (await store.UpsertAsync(Document(6, "evt-6", epoch: 0))).Disposition.Should().Be(ProjectionWriteDisposition.Applied, "version wins over epoch (old binary after rollback)");
        (await store.UpsertAsync(Document(6, "evt-6", epoch: 2))).Disposition.Should().Be(ProjectionWriteDisposition.Applied, "takeover again on roll-forward");
        (await store.UpsertAsync(Document(6, "evt-6", epoch: 2))).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);

        var stored = await store.GetAsync(ActorId);
        stored!.StateVersion.Should().Be(6);
        ((IProjectionRouteFencedReadModel)stored).RouteEpoch.Should().Be(2);
    }

    // ── unchanged behaviour ─────────────────────────────────────────────────────────────

    [Fact]
    public void RouteLessDocuments_HaveEpochZero_AndAreDecidedLikeThePlainEvaluator()
    {
        var v5 = Document(5, "evt-5", epoch: 0);
        ((IProjectionRouteFencedReadModel)v5).RouteEpoch.Should().Be(0);
        v5.StatusRoute.Should().BeNull();

        foreach (var incoming in new[]
                 {
                     Document(5, "evt-5", epoch: 0),
                     Document(5, "evt-5-other-fact", epoch: 0),
                     Document(5, "evt-5", epoch: 0, highestSeenVersion: 51),
                     Document(4, "evt-4", epoch: 0),
                     Document(6, "evt-6", epoch: 0),
                 })
        {
            Evaluate(v5, incoming).Should().Be(Plain(v5, incoming), "two B0/B2 documents are decided as before");
        }

        Evaluate(null, v5).Should().Be(ProjectionWriteDisposition.Applied);
        Evaluate(v5, Document(5, "evt-5", epoch: 0, actorId: "scope:other")).Should().Be(ProjectionWriteDisposition.Conflict, "actor identity still precedes everything");
    }

    [Fact]
    public void NonFencedReadModel_IsDecidedExactlyLikeThePlainEvaluator()
    {
        var existing = PlainReadModel(version: 7, "event-7", "stable");
        var cases = new[]
        {
            PlainReadModel(7, "event-7", "stable"),
            PlainReadModel(7, "event-7", "changed"),
            PlainReadModel(7, "event-7-other", "stable"),
            PlainReadModel(6, "event-6", "stable"),
            PlainReadModel(8, "event-8", "stable"),
        };

        cases.Select(incoming => Evaluate(existing, incoming)).Should().Equal(
            ProjectionWriteDisposition.Duplicate,
            ProjectionWriteDisposition.Conflict,
            ProjectionWriteDisposition.Conflict,
            ProjectionWriteDisposition.Stale,
            ProjectionWriteDisposition.Applied);
        cases.Select(incoming => Evaluate(existing, incoming)).Should().Equal(
            cases.Select(incoming => Plain(existing, incoming)),
            "a read model without a route epoch is not fenced");
    }

    // ── builders ────────────────────────────────────────────────────────────────────────

    private static ProjectionWriteDisposition Evaluate(IProjectionReadModel? existing, IProjectionReadModel incoming) =>
        ProjectionWriteResultEvaluator.Evaluate(existing, incoming).Disposition;

    private static ProjectionWriteDisposition Plain(IProjectionReadModel? existing, IProjectionReadModel incoming) =>
        PreviousBinaryProjectionWriteResultEvaluator.Evaluate(existing, incoming).Disposition;

    /// <summary>
    /// A status document as one writer maps it: epoch 0 means "no route carried" (B0/B2 shape);
    /// a positive epoch carries a terminal route at that epoch (only the epoch matters to the
    /// fence). Every other field is fixed so the only byte differences come from the arguments.
    /// </summary>
    private static ProjectionScopeStatusDocument Document(
        long version,
        string eventId,
        long epoch,
        long highestSeenVersion = 50,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Unspecified,
        string actorId = ActorId)
    {
        var document = new ProjectionScopeStatusDocument
        {
            Id = actorId,
            ScopeActorId = actorId,
            StateVersion = version,
            LastEventId = eventId,
            UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(Now.AddSeconds(version)),
            RootActorId = "root-actor",
            ProjectionKind = "kind",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            ObservationAttached = true,
            HighestSeenVersion = highestSeenVersion,
            LastSuccessfulVersion = highestSeenVersion - 1,
        };
        if (epoch > 0)
        {
            document.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(epoch, phase);
            document.StatusRoute.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        }

        return document;
    }

    private static TestStoreReadModel PlainReadModel(long version, string eventId, string value) => new()
    {
        Id = "conversation-alpha",
        ActorId = "conversation-alpha",
        StateVersion = version,
        LastEventId = eventId,
        UpdatedAt = Now,
        Value = value,
    };
}
