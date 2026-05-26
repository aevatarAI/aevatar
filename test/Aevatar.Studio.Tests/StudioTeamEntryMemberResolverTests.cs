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
            new MemberQueryPort(NewMember()));

        var result = await resolver.ResolveAsync(ScopeId, TeamId);

        result.ScopeId.Should().Be(ScopeId);
        result.TeamId.Should().Be(TeamId);
        result.EntryMemberId.Should().Be(EntryMemberId);
        result.PublishedServiceId.Should().Be(PublishedServiceId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowTeamNotFound_WhenTeamMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(null),
            new MemberQueryPort(NewMember()));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.TeamNotFound);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowTeamArchived_WhenTeamArchived()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam(lifecycleStage: TeamLifecycleStageNames.Archived)),
            new MemberQueryPort(NewMember()));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.TeamArchived);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotConfigured_WhenEntryMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam(entryMemberId: null)),
            new MemberQueryPort(NewMember()));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotConfigured);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowMemberNotFound_WhenMemberMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(null));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotFound);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowMismatch_WhenMemberBelongsToAnotherTeam()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(teamId: "other-team")));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberMismatch);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotReady_WhenMemberNotBindReady()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(lifecycleStage: MemberLifecycleStageNames.BuildReady)));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNotReady_WhenPublishedServiceMissing()
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember(publishedServiceId: "")));

        var act = () => resolver.ResolveAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady);
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

    private sealed class TeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, team == null ? [] : [team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult(team);
    }

    private sealed class MemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, member == null ? [] : [member.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(member);
    }
}
