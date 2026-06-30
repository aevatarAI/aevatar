using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeStatusListQueryPortTests
{
    [Fact]
    public void Constructor_ThrowsWhenDocumentReaderIsNull()
    {
        var act = () => new ProjectionScopeStatusListQueryPort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ListAsync_ThrowsWhenQueryIsNull()
    {
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader());

        var act = () => sut.ListAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyListWhenReadModelHasNoDocuments()
    {
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader());

        var snapshots = await sut.ListAsync(new ProjectionScopeStatusListQuery());

        snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_RequestsNewestUpdatedFirstWithIdTiebreakerAndTotalCount()
    {
        var reader = new CapturingReader();
        var sut = new ProjectionScopeStatusListQueryPort(reader);

        await sut.ListAsync(new ProjectionScopeStatusListQuery { Take = 10 });

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Sorts.Should().HaveCount(2);
        reader.LastQuery.Sorts[0].FieldPath.Should().Be(nameof(ProjectionScopeStatusDocument.UpdatedAt));
        reader.LastQuery.Sorts[0].Direction.Should().Be(ProjectionDocumentSortDirection.Desc);
        reader.LastQuery.Sorts[1].FieldPath.Should().Be(nameof(ProjectionScopeStatusDocument.Id));
        reader.LastQuery.Sorts[1].Direction.Should().Be(ProjectionDocumentSortDirection.Asc);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-1, 200)]
    [InlineData(50, 50)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(int.MaxValue, 200)]
    public async Task ListAsync_BoundsTakeBetweenOneAndTheHardCap(int requestedTake, int expectedTake)
    {
        var reader = new CapturingReader();
        var sut = new ProjectionScopeStatusListQueryPort(reader);

        await sut.ListAsync(new ProjectionScopeStatusListQuery { Take = requestedTake });

        reader.LastQuery!.Take.Should().Be(expectedTake);
    }

    [Fact]
    public async Task ListAsync_MapsDocumentFieldsIntoSnapshot()
    {
        var updated = new DateTimeOffset(2026, 5, 20, 4, 5, 6, TimeSpan.Zero);
        var document = new ProjectionScopeStatusDocument
        {
            ScopeActorId = "scope-a",
            Active = true,
            LastObservedVersion = 30,
            LastSuccessfulVersion = 25,
            FailureCount = 2,
            UpdatedAt = updated,
        };
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader(document));

        var snapshot = (await sut.ListAsync(new ProjectionScopeStatusListQuery())).Should().ContainSingle().Subject;

        snapshot.ScopeActorId.Should().Be("scope-a");
        snapshot.Active.Should().BeTrue();
        snapshot.LastObservedVersion.Should().Be(30);
        snapshot.LastSuccessfulVersion.Should().Be(25);
        snapshot.FailureCount.Should().Be(2);
        snapshot.Lag.Should().Be(5);
        snapshot.UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public async Task ListAsync_ComputesLagAsObservedMinusSuccessful()
    {
        var document = new ProjectionScopeStatusDocument
        {
            ScopeActorId = "scope-lag",
            LastObservedVersion = 100,
            LastSuccessfulVersion = 60,
        };
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader(document));

        var snapshot = (await sut.ListAsync(new ProjectionScopeStatusListQuery())).Single();

        snapshot.Lag.Should().Be(40);
    }

    [Fact]
    public async Task ListAsync_ClampsNegativeLagToZeroWhenSuccessfulLeadsObserved()
    {
        // Successful ahead of observed (e.g. a fresh observation watermark not yet recorded) must not
        // surface as a negative backlog.
        var document = new ProjectionScopeStatusDocument
        {
            ScopeActorId = "scope-ahead",
            LastObservedVersion = 10,
            LastSuccessfulVersion = 17,
        };
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader(document));

        var snapshot = (await sut.ListAsync(new ProjectionScopeStatusListQuery())).Single();

        snapshot.Lag.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ReturnsZeroLagWhenObservedEqualsSuccessful()
    {
        var document = new ProjectionScopeStatusDocument
        {
            ScopeActorId = "scope-caught-up",
            LastObservedVersion = 12,
            LastSuccessfulVersion = 12,
        };
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader(document));

        var snapshot = (await sut.ListAsync(new ProjectionScopeStatusListQuery())).Single();

        snapshot.Lag.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_PreservesReaderOrderAndMapsEveryDocument()
    {
        var first = new ProjectionScopeStatusDocument { ScopeActorId = "first" };
        var second = new ProjectionScopeStatusDocument { ScopeActorId = "second" };
        var sut = new ProjectionScopeStatusListQueryPort(new CapturingReader(first, second));

        var snapshots = await sut.ListAsync(new ProjectionScopeStatusListQuery());

        snapshots.Select(snapshot => snapshot.ScopeActorId).Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task ListAsync_StillReturnsItemsWhenReadModelReportsTruncation()
    {
        var document = new ProjectionScopeStatusDocument { ScopeActorId = "scope-truncated" };
        // TotalCount exceeds the returned item count: the impl logs a truncation warning but must still
        // return the page it has.
        var reader = new CapturingReader(items: [document], totalCount: 999);
        var sut = new ProjectionScopeStatusListQueryPort(reader);

        var snapshots = await sut.ListAsync(new ProjectionScopeStatusListQuery { Take = 1 });

        snapshots.Should().ContainSingle().Which.ScopeActorId.Should().Be("scope-truncated");
    }

    [Fact]
    public async Task ListAsync_ForwardsCancellationTokenToReader()
    {
        using var cts = new CancellationTokenSource();
        var reader = new CapturingReader();
        var sut = new ProjectionScopeStatusListQueryPort(reader);

        await sut.ListAsync(new ProjectionScopeStatusListQuery(), cts.Token);

        reader.ObservedToken.Should().Be(cts.Token);
    }

    private sealed class CapturingReader : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private readonly IReadOnlyList<ProjectionScopeStatusDocument> _items;
        private readonly long? _totalCount;

        public CapturingReader(params ProjectionScopeStatusDocument[] items)
        {
            _items = items;
            _totalCount = items.Length;
        }

        public CapturingReader(IReadOnlyList<ProjectionScopeStatusDocument> items, long? totalCount)
        {
            _items = items;
            _totalCount = totalCount;
        }

        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeStatusDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            ObservedToken = ct;
            return Task.FromResult(new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>
            {
                Items = _items,
                TotalCount = _totalCount,
            });
        }
    }
}
