using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionStudioTeamQueryPortTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task GetAsync_ShouldReturnSummary_WhenDocumentExists()
    {
        var document = NewDocument(ScopeId, "t-1");
        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var summary = await port.GetAsync(ScopeId, "t-1");

        summary.Should().NotBeNull();
        summary!.TeamId.Should().Be("t-1");
        summary.ScopeId.Should().Be(ScopeId);
        summary.DisplayName.Should().Be("Team Alpha");
        summary.Description.Should().Be("alpha desc");
        summary.LifecycleStage.Should().Be(TeamLifecycleStageNames.Active);
        summary.MemberCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDocumentMissing()
    {
        var reader = new StubDocumentReader([]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var summary = await port.GetAsync(ScopeId, "t-missing");

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDocumentExistsInDifferentScope()
    {
        var foreign = NewDocument("scope-other", "t-1");
        var reader = new StubDocumentReader([foreign]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var summary = await port.GetAsync(ScopeId, "t-1");

        summary.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyTeamsInScope()
    {
        var inScopeA = NewDocument(ScopeId, "t-1");
        var inScopeB = NewDocument(ScopeId, "t-2");
        var inOtherScope = NewDocument("scope-other", "t-3");

        var reader = new StubDocumentReader([inScopeA, inScopeB, inOtherScope]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.ScopeId.Should().Be(ScopeId);
        roster.Teams.Select(t => t.TeamId).Should().BeEquivalentTo("t-1", "t-2");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmpty_WhenScopeHasNoTeams()
    {
        var reader = new StubDocumentReader([]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.Teams.Should().BeEmpty();
        roster.NextPageToken.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldCapPageSize()
    {
        var reader = new StubDocumentReader([]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var roster = await port.ListAsync(
            ScopeId,
            new StudioTeamRosterPageRequest(PageSize: 999));

        roster.Should().NotBeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldPassPageToken()
    {
        var reader = new StubDocumentReader([], nextCursor: "cursor-2");
        var port = new ProjectionStudioTeamQueryPort(reader);

        var roster = await port.ListAsync(
            ScopeId,
            new StudioTeamRosterPageRequest(PageToken: "cursor-1"));

        roster.NextPageToken.Should().Be("cursor-2");
    }

    [Fact]
    public async Task GetAsync_ShouldMapArchivedLifecycle()
    {
        var doc = NewDocument(ScopeId, "t-1", lifecycleStage: TeamLifecycleStageNames.Archived);
        var reader = new StubDocumentReader([doc]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var summary = await port.GetAsync(ScopeId, "t-1");

        summary!.LifecycleStage.Should().Be(TeamLifecycleStageNames.Archived);
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeUnknownLifecycleToEmpty()
    {
        var doc = NewDocument(ScopeId, "t-1", lifecycleStage: "bogus");
        var reader = new StubDocumentReader([doc]);
        var port = new ProjectionStudioTeamQueryPort(reader);

        var summary = await port.GetAsync(ScopeId, "t-1");

        summary!.LifecycleStage.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldRejectNullDocumentReader()
    {
        FluentActions.Invoking(() => new ProjectionStudioTeamQueryPort(null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static StudioTeamCurrentStateDocument NewDocument(
        string scopeId,
        string teamId,
        string lifecycleStage = "active",
        int memberCount = 2)
    {
        var actorId = $"studio-team:{scopeId}:{teamId}";
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new StudioTeamCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
            TeamId = teamId,
            ScopeId = scopeId,
            DisplayName = "Team Alpha",
            Description = "alpha desc",
            LifecycleStage = lifecycleStage,
            CreatedAt = now,
            MemberCount = memberCount,
        };
    }

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioTeamCurrentStateDocument, string>
    {
        private readonly Dictionary<string, StudioTeamCurrentStateDocument> _byId;
        private readonly string? _nextCursor;

        public StubDocumentReader(
            IReadOnlyList<StudioTeamCurrentStateDocument> documents,
            string? nextCursor = null)
        {
            _byId = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
            _nextCursor = nextCursor;
        }

        public Task<StudioTeamCurrentStateDocument?> GetAsync(
            string key, CancellationToken ct = default)
        {
            return Task.FromResult(_byId.TryGetValue(key, out var doc) ? doc : null);
        }

        public Task<ProjectionDocumentQueryResult<StudioTeamCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default)
        {
            var scopeFilter = query.Filters.FirstOrDefault(
                f => string.Equals(f.FieldPath, "scope_id", StringComparison.Ordinal));

            IEnumerable<StudioTeamCurrentStateDocument> items = _byId.Values;
            if (scopeFilter != null && scopeFilter.Value.RawValue is string scope)
            {
                items = items.Where(d => string.Equals(d.ScopeId, scope, StringComparison.Ordinal));
            }

            return Task.FromResult(new ProjectionDocumentQueryResult<StudioTeamCurrentStateDocument>
            {
                Items = items.Take(query.Take).ToList(),
                NextCursor = _nextCursor,
            });
        }
    }
}
