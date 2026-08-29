using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionFailureRecoveryReconcilerTests
{
    [Fact]
    public void Constructor_ShouldRejectMissingDependencies()
    {
        var reader = new PagingReader();
        var replayService = new RecordingReplayService();

        var missingReader = () => new ProjectionFailureRecoveryReconciler(null!, replayService);
        var missingReplayService = () => new ProjectionFailureRecoveryReconciler(reader, null!);

        missingReader.Should().Throw<ArgumentNullException>();
        missingReplayService.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldPageAndDispatchOnlyEligibleConsistentScopes()
    {
        var sessionScope = new ProjectionRuntimeScopeKey(
            "root-session",
            "projection-session",
            ProjectionRuntimeMode.SessionObservation,
            "session-alpha");
        var durableScope = new ProjectionRuntimeScopeKey(
            "root-durable",
            "projection-durable",
            ProjectionRuntimeMode.DurableMaterialization);
        var inconsistentScope = BuildCandidate(
            new ProjectionRuntimeScopeKey(
                "root-inconsistent",
                "projection-inconsistent",
                ProjectionRuntimeMode.DurableMaterialization),
            unresolvedFailureCount: 1,
            retryExhaustedFailureCount: 0,
            stateVersion: 2);
        inconsistentScope.ScopeActorId = "projection.durable.scope:wrong";

        var reader = new PagingReader(
            new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                Items =
                [
                    BuildCandidate(sessionScope, 5, 2, 19),
                    BuildCandidate(
                        new ProjectionRuntimeScopeKey(
                            "root-inactive",
                            "projection-inactive",
                            ProjectionRuntimeMode.DurableMaterialization),
                        1,
                        0,
                        3,
                        active: false),
                    inconsistentScope,
                ],
                NextCursor = "page-2",
            },
            new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                Items =
                [
                    BuildCandidate(durableScope, 150, 0, 0),
                    BuildCandidate(
                        new ProjectionRuntimeScopeKey(
                            "root-exhausted",
                            "projection-exhausted",
                            ProjectionRuntimeMode.DurableMaterialization),
                        4,
                        4,
                        12),
                ],
            });
        var replayService = new RecordingReplayService();
        var reconciler = new ProjectionFailureRecoveryReconciler(reader, replayService);

        var dispatched = await reconciler.ReconcileAsync();

        dispatched.Should().Be(2);
        replayService.AutomaticCalls.Should().HaveCount(2);
        replayService.AutomaticCalls[0].ScopeKey.Should().Be(sessionScope);
        replayService.AutomaticCalls[0].ObservedScopeStateVersion.Should().Be(19);
        replayService.AutomaticCalls[0].MaxItems.Should().Be(3);
        replayService.AutomaticCalls[1].ScopeKey.Should().Be(durableScope);
        replayService.AutomaticCalls[1].ObservedScopeStateVersion.Should().Be(1);
        replayService.AutomaticCalls[1].MaxItems.Should().Be(
            ProjectionFailureRecoveryReconciler.MaxReplayItemsPerScope);

        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Cursor.Should().BeNull();
        reader.Queries[1].Cursor.Should().Be("page-2");
        reader.Queries.Should().OnlyContain(query => query.Take == ProjectionFailureRecoveryReconciler.PageSize);

        var firstQuery = reader.Queries[0];
        firstQuery.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(ProjectionScopeStatusDocument.Active) &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq &&
            Equals(filter.Value.RawValue, true));
        firstQuery.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(ProjectionScopeStatusDocument.Released) &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq &&
            Equals(filter.Value.RawValue, false));
        firstQuery.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(ProjectionScopeStatusDocument.UnresolvedFailureCount) &&
            filter.Operator == ProjectionDocumentFilterOperator.Gt &&
            Equals(filter.Value.RawValue, 0L));
        firstQuery.Sorts.Select(sort => (sort.FieldPath, sort.Direction)).Should().Equal(
            (nameof(ProjectionScopeStatusDocument.RetryExhaustedFailureCount), ProjectionDocumentSortDirection.Asc),
            (nameof(ProjectionScopeStatusDocument.OldestUnresolvedFailureAtUtc), ProjectionDocumentSortDirection.Asc),
            (nameof(ProjectionScopeStatusDocument.Id), ProjectionDocumentSortDirection.Asc));
    }

    [Fact]
    public async Task ReconcileAsync_WhenOneDispatchFails_ShouldContinueWithRemainingScopes()
    {
        var firstScope = new ProjectionRuntimeScopeKey(
            "root-first",
            "projection-first",
            ProjectionRuntimeMode.DurableMaterialization);
        var secondScope = new ProjectionRuntimeScopeKey(
            "root-second",
            "projection-second",
            ProjectionRuntimeMode.DurableMaterialization);
        var reader = new PagingReader(new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
        {
            Items =
            [
                BuildCandidate(firstScope, 1, 0, 1),
                BuildCandidate(secondScope, 1, 0, 1),
            ],
        });
        var replayService = new RecordingReplayService
        {
            ThrowOnRootActorId = firstScope.RootActorId,
        };
        var reconciler = new ProjectionFailureRecoveryReconciler(reader, replayService);

        var dispatched = await reconciler.ReconcileAsync();

        dispatched.Should().Be(1);
        replayService.AutomaticCalls.Select(call => call.ScopeKey).Should().Equal(firstScope, secondScope);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldContinuePastMoreThanTwentyFiveThousandExhaustedCandidates()
    {
        var eligibleScope = new ProjectionRuntimeScopeKey(
            "root-eligible-tail",
            "projection-eligible-tail",
            ProjectionRuntimeMode.DurableMaterialization);
        var reader = new VirtualExhaustedPrefixReader(
            exhaustedCandidateCount: 25_200,
            eligibleTail: BuildCandidate(eligibleScope, 1, 0, 41));
        var replayService = new RecordingReplayService();
        var reconciler = new ProjectionFailureRecoveryReconciler(reader, replayService);

        var dispatched = await reconciler.ReconcileAsync();

        dispatched.Should().Be(1);
        replayService.AutomaticCalls.Should().ContainSingle();
        replayService.AutomaticCalls[0].ScopeKey.Should().Be(eligibleScope);
        replayService.AutomaticCalls[0].ObservedScopeStateVersion.Should().Be(41);
        reader.QueryCount.Should().Be(127);
    }

    [Fact]
    public async Task ReconcileAsync_WhenCursorDoesNotAdvance_ShouldStopPaging()
    {
        var reader = new PagingReader(
            new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                NextCursor = "stuck-cursor",
            },
            new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                NextCursor = "stuck-cursor",
            });
        var replayService = new RecordingReplayService();
        var reconciler = new ProjectionFailureRecoveryReconciler(reader, replayService);

        var dispatched = await reconciler.ReconcileAsync();

        dispatched.Should().Be(0);
        replayService.AutomaticCalls.Should().BeEmpty();
        reader.Queries.Select(query => query.Cursor).Should().Equal(null, "stuck-cursor");
    }

    private static ProjectionScopeStatusDocument BuildCandidate(
        ProjectionRuntimeScopeKey scopeKey,
        int unresolvedFailureCount,
        int retryExhaustedFailureCount,
        long stateVersion,
        bool active = true)
    {
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        return new ProjectionScopeStatusDocument
        {
            Id = scopeActorId,
            ScopeActorId = scopeActorId,
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            SessionId = scopeKey.SessionId ?? string.Empty,
            Mode = scopeKey.Mode == ProjectionRuntimeMode.SessionObservation
                ? ProjectionScopeMode.SessionObservation
                : ProjectionScopeMode.DurableMaterialization,
            Active = active,
            Released = false,
            UnresolvedFailureCount = unresolvedFailureCount,
            RetryExhaustedFailureCount = retryExhaustedFailureCount,
            StateVersion = stateVersion,
        };
    }

    private sealed class PagingReader
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private readonly Queue<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> _pages;

        public PagingReader(params ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>[] pages)
        {
            _pages = new Queue<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>>(pages);
        }

        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeStatusDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(
                _pages.Count == 0
                    ? ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>.Empty
                    : _pages.Dequeue());
        }
    }

    private sealed class VirtualExhaustedPrefixReader
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private readonly int _exhaustedPageCount;
        private readonly ProjectionScopeStatusDocument _eligibleTail;
        private readonly IReadOnlyList<ProjectionScopeStatusDocument> _exhaustedPage;

        public VirtualExhaustedPrefixReader(
            int exhaustedCandidateCount,
            ProjectionScopeStatusDocument eligibleTail)
        {
            exhaustedCandidateCount.Should().BePositive();
            (exhaustedCandidateCount % ProjectionFailureRecoveryReconciler.PageSize).Should().Be(0);
            _exhaustedPageCount = exhaustedCandidateCount / ProjectionFailureRecoveryReconciler.PageSize;
            _eligibleTail = eligibleTail;
            var exhaustedScope = new ProjectionRuntimeScopeKey(
                "root-exhausted-prefix",
                "projection-exhausted-prefix",
                ProjectionRuntimeMode.DurableMaterialization);
            _exhaustedPage = new RepeatedReadOnlyList<ProjectionScopeStatusDocument>(
                BuildCandidate(exhaustedScope, 1, 1, 1),
                ProjectionFailureRecoveryReconciler.PageSize);
        }

        public int QueryCount { get; private set; }

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeStatusDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            QueryCount++;
            if (QueryCount <= _exhaustedPageCount)
            {
                return Task.FromResult(new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
                {
                    Items = _exhaustedPage,
                    NextCursor = $"page-{QueryCount}",
                });
            }

            return Task.FromResult(new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                Items = [_eligibleTail],
            });
        }
    }

    private sealed class RepeatedReadOnlyList<T>(T item, int count) : IReadOnlyList<T>
    {
        public int Count { get; } = count;

        public T this[int index] =>
            index >= 0 && index < Count
                ? item
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingReplayService : IProjectionFailureReplayService
    {
        public List<AutomaticReplayCall> AutomaticCalls { get; } = [];

        public string? ThrowOnRootActorId { get; init; }

        public Task<bool> ReplayRetryExhaustedAsync(
            ProjectionRetryExhaustedFailuresRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> ReplayAutomaticallyAsync(
            ProjectionRuntimeScopeKey scopeKey,
            long observedScopeStateVersion,
            int maxItems = 100,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AutomaticCalls.Add(new AutomaticReplayCall(scopeKey, observedScopeStateVersion, maxItems));
            if (string.Equals(scopeKey.RootActorId, ThrowOnRootActorId, StringComparison.Ordinal))
                throw new InvalidOperationException("dispatch failed");

            return Task.FromResult(true);
        }
    }

    private sealed record AutomaticReplayCall(
        ProjectionRuntimeScopeKey ScopeKey,
        long ObservedScopeStateVersion,
        int MaxItems);
}
