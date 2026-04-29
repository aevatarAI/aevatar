using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberServiceTeamTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-team-test";
    private const string PublishedServiceId = "member-m-team-test";
    private const string TeamId = "t-1";

    [Fact]
    public async Task CreateAsync_WithTeamId_ShouldRejectNonExistentTeam()
    {
        var teamQueryPort = new InMemoryTeamQueryPort(team: null);
        var service = NewService(teamQueryPort: teamQueryPort);

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                TeamId: "non-existent-team"));

        await act.Should().ThrowAsync<StudioTeamNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_WithTeamId_ShouldSucceedWhenTeamExists()
    {
        var team = NewTeamSummary();
        var teamQueryPort = new InMemoryTeamQueryPort(team);
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort: commandPort, teamQueryPort: teamQueryPort);

        var result = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                TeamId: TeamId));

        result.Should().NotBeNull();
        commandPort.CreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithoutTeamId_ShouldNotQueryTeam()
    {
        var teamQueryPort = new ThrowingTeamQueryPort();
        var service = NewService(teamQueryPort: teamQueryPort);

        var result = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow));

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldDispatchReassignment_WhenTeamIdChanges()
    {
        var detail = NewDetail(currentTeamId: "old-team");
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort: commandPort,
            memberQueryPort: new InMemoryMemberQueryPort(detail));

        await service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(TeamId: PatchValue<string>.Of("new-team")));

        commandPort.ReassignCalls.Should().Be(1);
        commandPort.LastFromTeamId.Should().Be("old-team");
        commandPort.LastToTeamId.Should().Be("new-team");
    }

    [Fact]
    public async Task UpdateAsync_ShouldNoOp_WhenTeamIdAlreadyMatches()
    {
        var detail = NewDetail(currentTeamId: TeamId);
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort: commandPort,
            memberQueryPort: new InMemoryMemberQueryPort(detail));

        var result = await service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(TeamId: PatchValue<string>.Of(TeamId)));

        commandPort.ReassignCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectEmptyTeamId()
    {
        var detail = NewDetail();
        var service = NewService(memberQueryPort: new InMemoryMemberQueryPort(detail));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(TeamId: PatchValue<string>.Of("  ")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*teamId must not be empty*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldUnassign_WhenTeamIdNull()
    {
        var detail = NewDetail(currentTeamId: TeamId);
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort: commandPort,
            memberQueryPort: new InMemoryMemberQueryPort(detail));

        await service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(TeamId: PatchValue<string>.Of(null)));

        commandPort.ReassignCalls.Should().Be(1);
        commandPort.LastFromTeamId.Should().Be(TeamId);
        commandPort.LastToTeamId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenMemberNotFound()
    {
        var service = NewService(memberQueryPort: new InMemoryMemberQueryPort(null));

        var act = () => service.UpdateAsync(
            ScopeId,
            "missing-member",
            new UpdateStudioMemberRequest(TeamId: PatchValue<string>.Of("t-1")));

        await act.Should().ThrowAsync<StudioMemberNotFoundException>();
    }

    private static StudioMemberService NewService(
        RecordingMemberCommandPort? commandPort = null,
        InMemoryMemberQueryPort? memberQueryPort = null,
        IStudioTeamQueryPort? teamQueryPort = null) =>
        new(
            commandPort ?? new RecordingMemberCommandPort(),
            memberQueryPort ?? new InMemoryMemberQueryPort(NewDetail()),
            teamQueryPort ?? new InMemoryTeamQueryPort(NewTeamSummary()),
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingServiceCommandPort());

    private static StudioMemberDetailResponse NewDetail(string? currentTeamId = null)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: MemberId,
            ScopeId: ScopeId,
            DisplayName: "Test Member",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: MemberLifecycleStageNames.Created,
            PublishedServiceId: PublishedServiceId,
            LastBoundRevisionId: null,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow)
        { TeamId = currentTeamId };

        return new StudioMemberDetailResponse(
            Summary: summary,
            ImplementationRef: null,
            LastBinding: null);
    }

    private static StudioTeamSummaryResponse NewTeamSummary() =>
        new(
            TeamId: TeamId,
            ScopeId: ScopeId,
            DisplayName: "Team Alpha",
            Description: string.Empty,
            LifecycleStage: TeamLifecycleStageNames.Active,
            MemberCount: 0,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class InMemoryMemberQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberDetailResponse? _detail;

        public InMemoryMemberQueryPort(StudioMemberDetailResponse? detail) => _detail = detail;

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, _detail == null ? [] : [_detail.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(_detail);
    }

    private sealed class InMemoryTeamQueryPort : IStudioTeamQueryPort
    {
        private readonly StudioTeamSummaryResponse? _team;

        public InMemoryTeamQueryPort(StudioTeamSummaryResponse? team) => _team = team;

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, _team == null ? [] : [_team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_team);
    }

    private sealed class ThrowingTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("team query port should not be called");

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            throw new InvalidOperationException("team query port should not be called");
    }

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        public int CreateCalls { get; private set; }
        public int ReassignCalls { get; private set; }
        public string? LastFromTeamId { get; private set; }
        public string? LastToTeamId { get; private set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new StudioMemberSummaryResponse(
                MemberId: MemberId,
                ScopeId: scopeId,
                DisplayName: request.DisplayName ?? string.Empty,
                Description: request.Description ?? string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: PublishedServiceId,
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<StudioMemberBindingAcceptedResponse> RequestBindingAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task UpdateImplementationAsync(
            string scopeId, string memberId,
            StudioMemberImplementationRefResponse implementation, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordBindingAsync(
            string scopeId, string memberId, string publishedServiceId,
            string revisionId, string implementationKindName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReassignTeamAsync(
            string scopeId, string memberId, string? fromTeamId, string? toTeamId,
            CancellationToken ct = default)
        {
            ReassignCalls++;
            LastFromTeamId = fromTeamId;
            LastToTeamId = toTeamId;
            return Task.CompletedTask;
        }

        public Task CompleteBindingAsync(
            string scopeId, string memberId, StudioMemberBindingCompletionRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task FailBindingAsync(
            string scopeId, string memberId, StudioMemberBindingFailureRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("not expected");
        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            throw new InvalidOperationException("not expected");
        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("not expected");
        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("not expected");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string m) => new($"not expected: {m}");
        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand c, CancellationToken ct = default) => throw Reject(nameof(CreateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand c, CancellationToken ct = default) => throw Reject(nameof(UpdateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(CreateRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(PrepareRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(PublishRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(RetireRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(SetDefaultServingRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand c, CancellationToken ct = default) => throw Reject(nameof(ActivateServiceRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand c, CancellationToken ct = default) => throw Reject(nameof(DeactivateServiceDeploymentAsync));
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand c, CancellationToken ct = default) => throw Reject(nameof(ReplaceServiceServingTargetsAsync));
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand c, CancellationToken ct = default) => throw Reject(nameof(StartServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand c, CancellationToken ct = default) => throw Reject(nameof(AdvanceServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand c, CancellationToken ct = default) => throw Reject(nameof(PauseServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand c, CancellationToken ct = default) => throw Reject(nameof(ResumeServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand c, CancellationToken ct = default) => throw Reject(nameof(RollbackServiceRolloutAsync));
    }
}
