using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamEntryMemberResolverTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "t-1";
    private const string EntryMemberId = "m-1";
    private const string PublishedServiceId = "member-m-1";

    [Fact]
    public async Task ResolveAsync_ShouldReturnPublishedService_WhenEntryMemberReady()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember()),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var result = await resolver.ResolveAsync(ScopeId, TeamId);

        result.ScopeId.Should().Be(ScopeId);
        result.TeamId.Should().Be(TeamId);
        result.EntryMemberId.Should().Be(EntryMemberId);
        result.PublishedServiceId.Should().Be(PublishedServiceId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseTeamAndMemberReadModelsOnlyForCommandTargetAdmission()
    {
        var teamPort = new TeamQueryPort(NewTeam());
        var memberPort = new MemberQueryPort(NewMember());
        var resolver = new StudioTeamEntryMemberResolver(
            teamPort,
            memberPort,
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var result = await resolver.ResolveAsync(ScopeId, TeamId);

        result.Should().Be(new TeamEntryMemberResolution(
            ScopeId,
            TeamId,
            EntryMemberId,
            PublishedServiceId));
        teamPort.GetCalls.Should().Be(1);
        teamPort.ListCalls.Should().Be(0);
        memberPort.GetCalls.Should().Be(1);
        memberPort.ListCalls.Should().Be(0);
        teamPort.GetRequests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, TeamId));
        memberPort.GetRequests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, EntryMemberId));
    }

    [Fact]
    public void TeamEntryMemberResolution_ShouldRemainNarrowCommandTargetContract()
    {
        var propertyNames = typeof(TeamEntryMemberResolution)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        propertyNames.Should().BeEquivalentTo(
            [
                nameof(TeamEntryMemberResolution.ScopeId),
                nameof(TeamEntryMemberResolution.TeamId),
                nameof(TeamEntryMemberResolution.EntryMemberId),
                nameof(TeamEntryMemberResolution.PublishedServiceId),
            ],
            options => options.WithStrictOrdering(),
            "this resolver is command target resolution, not a composite team readiness/status read model");
        propertyNames.Should().NotContain(static name =>
            name.Contains("Status", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Readiness", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ready", StringComparison.OrdinalIgnoreCase)
            || name.Contains("StateVersion", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Version", StringComparison.OrdinalIgnoreCase)
            || name.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowTeamNotFound_WhenTeamMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(null),
            new MemberQueryPort(NewMember()),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.TeamNotFound);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowTeamArchived_WhenTeamArchived()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam(lifecycleStage: TeamLifecycleStageNames.Archived)),
            new MemberQueryPort(NewMember()),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.TeamArchived);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotConfigured_WhenEntryMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam(entryMemberId: null)),
            new MemberQueryPort(NewMember()),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotConfigured);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowMemberNotFound_WhenMemberMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(null),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotFound);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowMismatch_WhenMemberBelongsToAnotherTeam()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(teamId: "other-team")),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberMismatch);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotReady_WhenMemberNotBindReady()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(lifecycleStage: MemberLifecycleStageNames.BuildReady)),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotReady_WhenPublishedServiceMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(publishedServiceId: "")),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady);
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotPinReadinessToLastBoundRevision()
    {
        var readinessPort = new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.Ready, invokeReady: true);
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember()),
            readinessPort);

        await resolver.ResolveAsync(ScopeId, TeamId);

        readinessPort.LastRequest.Should().NotBeNull();
        readinessPort.LastRequest!.ExpectedRevisionId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotReady_WhenPreparedArtifactMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember()),
            new FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus.PreparedArtifactMissing, invokeReady: false));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady
                && ex.Message.Contains("prepared_artifact_missing", StringComparison.Ordinal));
    }

    private static StudioTeamSummaryResponse NewTeam(
        string lifecycleStage = TeamLifecycleStageNames.Active,
        string? entryMemberId = EntryMemberId) =>
        new(
            TeamId: TeamId,
            ScopeId: ScopeId,
            DisplayName: "Team Alpha",
            Description: string.Empty,
            LifecycleStage: lifecycleStage,
            MemberCount: 1,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            EntryMemberId = entryMemberId,
        };

    private static StudioMemberDetailResponse NewMember(
        string? teamId = TeamId,
        string lifecycleStage = MemberLifecycleStageNames.BindReady,
        string publishedServiceId = PublishedServiceId)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: EntryMemberId,
            ScopeId: ScopeId,
            DisplayName: "Member Alpha",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: lifecycleStage,
            PublishedServiceId: publishedServiceId,
            LastBoundRevisionId: "rev-1",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            TeamId = teamId,
        };

        return new StudioMemberDetailResponse(summary, null, null);
    }

    private sealed class FixedScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        private readonly ScopeBindingReadinessStatus _status;
        private readonly bool _invokeReady;

        public FixedScopeBindingReadinessQueryPort(ScopeBindingReadinessStatus status, bool invokeReady)
        {
            _status = status;
            _invokeReady = invokeReady;
        }

        public ScopeBindingReadinessRequest? LastRequest { get; private set; }

        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ScopeBindingReadinessSnapshot(
                request.ScopeId,
                request.ServiceId,
                _status,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: _invokeReady,
                RevisionId: request.ExpectedRevisionId ?? "rev-1",
                DeploymentId: "dep-1",
                ObservedAtUtc: DateTimeOffset.UtcNow));
        }
    }

    private sealed class TeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public List<(string ScopeId, string TeamId)> GetRequests { get; } = [];

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(new StudioTeamRosterResponse(scopeId, team == null ? [] : [team]));
        }

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            GetCalls++;
            GetRequests.Add((scopeId, teamId));
            return Task.FromResult(team);
        }
    }

    private sealed class MemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public List<(string ScopeId, string MemberId)> GetRequests { get; } = [];

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(new StudioMemberRosterResponse(scopeId, member == null ? [] : [member.Summary]));
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            GetCalls++;
            GetRequests.Add((scopeId, memberId));
            return Task.FromResult(member);
        }
    }
}
