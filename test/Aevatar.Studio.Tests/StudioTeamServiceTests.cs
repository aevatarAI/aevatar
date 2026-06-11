using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamServiceTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "t-1";
    private const string EntryMemberId = "m-1";

    [Fact]
    public async Task CreateAsync_ShouldValidateAndDelegate()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(NewSummary());
        var service = new StudioTeamService(commandPort, queryPort, new InMemoryMemberQueryPort(null));

        var result = await service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "Alpha"));

        result.Should().NotBeNull();
        result.TeamId.Should().Be(TeamId);
        commandPort.CreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyDisplayName()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(null),
            new InMemoryMemberQueryPort(null));

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "  "));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public async Task GetAsync_ShouldThrowNotFound_WhenTeamMissing()
    {
        var queryPort = new InMemoryQueryPort(summary: null);
        var service = new StudioTeamService(new RecordingCommandPort(), queryPort, new InMemoryMemberQueryPort(null));

        var act = () => service.GetAsync(ScopeId, "missing-team");

        await act.Should().ThrowAsync<StudioTeamNotFoundException>();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnSummary_WhenTeamExists()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(summary),
            new InMemoryMemberQueryPort(null));

        var result = await service.GetAsync(ScopeId, TeamId);

        result.Should().Be(summary);
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectEmptyDisplayName()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(summary),
            new InMemoryMemberQueryPort(null));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(DisplayName: PatchValue<string>.Of("  ")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName must not be empty*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectDisplayNameOverCap()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(summary),
            new InMemoryMemberQueryPort(null));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(
                DisplayName: PatchValue<string>.Of(
                    new string('a', StudioTeamInputLimits.MaxDisplayNameLength + 1))));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName must be at most*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectDescriptionOverCap()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(summary),
            new InMemoryMemberQueryPort(null));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(
                Description: PatchValue<string>.Of(
                    new string('a', StudioTeamInputLimits.MaxDescriptionLength + 1))));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*description must be at most*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldReturnAcceptedReceiptWithoutPostDispatchRead()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(null);
        var service = new StudioTeamService(
            commandPort,
            queryPort,
            new InMemoryMemberQueryPort(null));

        var result = await service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(DisplayName: PatchValue<string>.Of("Beta")));

        commandPort.UpdateCalls.Should().Be(1);
        queryPort.GetCalls.Should().Be(0);
        result.Status.Should().Be(StudioTeamCommandStatusNames.Accepted);
        result.TeamId.Should().Be(TeamId);
        result.CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task ArchiveAsync_ShouldReturnAcceptedReceiptWithoutPostDispatchRead()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(null);
        var service = new StudioTeamService(
            commandPort,
            queryPort,
            new InMemoryMemberQueryPort(null));

        var result = await service.ArchiveAsync(ScopeId, TeamId);

        commandPort.ArchiveCalls.Should().Be(1);
        queryPort.GetCalls.Should().Be(0);
        result.Status.Should().Be(StudioTeamCommandStatusNames.Accepted);
        result.TeamId.Should().Be(TeamId);
        result.CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldValidateTeamAndMemberThenDelegateWithoutPostDispatchRead()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(NewSummary());
        var member = NewMember(TeamId);
        var service = new StudioTeamService(
            commandPort,
            queryPort,
            new InMemoryMemberQueryPort(member));

        await service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest(EntryMemberId));

        commandPort.SetEntryCalls.Should().Be(1);
        commandPort.LastEntryMemberId.Should().Be(EntryMemberId);
        queryPort.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldRejectMissingTeam()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(null),
            new InMemoryMemberQueryPort(NewMember(TeamId)));

        var act = () => service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest(EntryMemberId));

        await act.Should().ThrowAsync<StudioTeamNotFoundException>();
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldRejectMissingMember()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(NewSummary()),
            new InMemoryMemberQueryPort(null));

        var act = () => service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest(EntryMemberId));

        await act.Should().ThrowAsync<StudioMemberNotFoundException>();
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldRejectMemberOutsideTeam()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(NewSummary()),
            new InMemoryMemberQueryPort(NewMember("other-team")));

        var act = () => service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest(EntryMemberId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to team*");
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldRejectArchivedTeam()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(NewSummary(TeamLifecycleStageNames.Archived)),
            new InMemoryMemberQueryPort(NewMember(TeamId)));

        var act = () => service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest(EntryMemberId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*archived*");
    }

    [Fact]
    public async Task SetEntryMemberAsync_ShouldRejectBlankMemberId()
    {
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(NewSummary()),
            new InMemoryMemberQueryPort(NewMember(TeamId)));

        var act = () => service.SetEntryMemberAsync(
            ScopeId,
            TeamId,
            new SetStudioTeamEntryMemberRequest("  "));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MemberId is required*");
    }

    [Fact]
    public async Task ClearEntryMemberAsync_ShouldValidateTeamThenDelegateWithoutPostDispatchRead()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(NewSummary());
        var service = new StudioTeamService(
            commandPort,
            queryPort,
            new InMemoryMemberQueryPort(NewMember(TeamId)));

        await service.ClearEntryMemberAsync(ScopeId, TeamId);

        commandPort.ClearEntryCalls.Should().Be(1);
        queryPort.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ClearEntryMemberAsync_ShouldRejectMissingTeam()
    {
        var commandPort = new RecordingCommandPort();
        var service = new StudioTeamService(
            commandPort,
            new InMemoryQueryPort(null),
            new InMemoryMemberQueryPort(null));

        var act = () => service.ClearEntryMemberAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<StudioTeamNotFoundException>();
        commandPort.ClearEntryCalls.Should().Be(0);
    }

    [Fact]
    public async Task ClearEntryMemberAsync_ShouldRejectArchivedTeam()
    {
        var commandPort = new RecordingCommandPort();
        var service = new StudioTeamService(
            commandPort,
            new InMemoryQueryPort(NewSummary(TeamLifecycleStageNames.Archived)),
            new InMemoryMemberQueryPort(null));

        var act = () => service.ClearEntryMemberAsync(ScopeId, TeamId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*archived*");
        commandPort.ClearEntryCalls.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldDelegate()
    {
        var summary = NewSummary();
        var queryPort = new InMemoryQueryPort(summary);
        var service = new StudioTeamService(
            new RecordingCommandPort(),
            queryPort,
            new InMemoryMemberQueryPort(null));

        var result = await service.ListAsync(ScopeId);

        result.Should().NotBeNull();
        result.Teams.Should().ContainSingle();
    }

    private static StudioTeamSummaryResponse NewSummary(
        string lifecycleStage = TeamLifecycleStageNames.Active) =>
        new(
            TeamId: TeamId,
            ScopeId: ScopeId,
            DisplayName: "Alpha",
            Description: "desc",
            LifecycleStage: lifecycleStage,
            MemberCount: 0,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            EntryMemberId = EntryMemberId,
        };

    private static StudioMemberDetailResponse NewMember(string? teamId)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: EntryMemberId,
            ScopeId: ScopeId,
            DisplayName: "Member",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: "member-m-1",
            LastBoundRevisionId: "rev-1",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            TeamId = teamId,
        };

        return new StudioMemberDetailResponse(summary, null, null);
    }

    private sealed class InMemoryQueryPort : IStudioTeamQueryPort
    {
        private readonly StudioTeamSummaryResponse? _summary;
        public int GetCalls { get; private set; }

        public InMemoryQueryPort(StudioTeamSummaryResponse? summary) => _summary = summary;

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, _summary == null ? [] : [_summary]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(_summary);
        }
    }

    private sealed class RecordingCommandPort : IStudioTeamCommandPort
    {
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int ArchiveCalls { get; private set; }
        public int SetEntryCalls { get; private set; }
        public int ClearEntryCalls { get; private set; }
        public string? LastEntryMemberId { get; private set; }

        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new StudioTeamSummaryResponse(
                TeamId: TeamId,
                ScopeId: scopeId,
                DisplayName: request.DisplayName ?? string.Empty,
                Description: request.Description ?? string.Empty,
                LifecycleStage: TeamLifecycleStageNames.Active,
                MemberCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<StudioTeamCommandResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default)
        {
            UpdateCalls++;
            return Task.FromResult(NewAccepted(scopeId, teamId));
        }

        public Task<StudioTeamCommandResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default)
        {
            ArchiveCalls++;
            return Task.FromResult(NewAccepted(scopeId, teamId));
        }

        public Task SetEntryMemberAsync(
            string scopeId,
            string teamId,
            string memberId,
            CancellationToken ct = default)
        {
            SetEntryCalls++;
            LastEntryMemberId = memberId;
            return Task.CompletedTask;
        }

        public Task ClearEntryMemberAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            ClearEntryCalls++;
            return Task.CompletedTask;
        }

        private static StudioTeamCommandResponse NewAccepted(string scopeId, string teamId) =>
            new(
                StudioTeamCommandStatusNames.Accepted,
                scopeId,
                teamId,
                "cmd-1",
                "corr-1",
                DateTimeOffset.UtcNow);
    }

    private sealed class InMemoryMemberQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberDetailResponse? _detail;

        public InMemoryMemberQueryPort(StudioMemberDetailResponse? detail) => _detail = detail;

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                _detail == null ? [] : [_detail.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(_detail);
    }
}
