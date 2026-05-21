using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamGAgentStreamInvocationServiceTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "team-1";
    private const string EntryMemberId = "member-1";
    private const string PublishedServiceId = "published-service-1";

    [Fact]
    public async Task InvokeAsync_ShouldResolveEntryMemberAndDelegateToPublishedServiceIdentity()
    {
        var staticPort = new RecordingStaticGAgentStreamInvocationPort();
        var service = CreateService(staticPort);
        var emitted = new List<AGUIEvent>();
        StaticGAgentStreamAcceptedReceipt? accepted = null;

        var result = await service.InvokeAsync(
            new StudioTeamGAgentStreamInvocationRequest(
                ScopeId,
                TeamId,
                "chat",
                new StaticGAgentStreamInvocationInput(
                    Prompt: "hello team",
                    SessionId: "session-1",
                    Headers: new Dictionary<string, string>
                    {
                        ["x-trace"] = "trace-1",
                    })),
            (frame, _) =>
            {
                emitted.Add(frame);
                return ValueTask.CompletedTask;
            },
            (receipt, _) =>
            {
                accepted = receipt;
                return ValueTask.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        accepted.Should().NotBeNull();
        emitted.Should().ContainSingle()
            .Which.RunStarted.RunId.Should().Be("cmd-1");

        staticPort.Requests.Should().ContainSingle();
        var delegated = staticPort.Requests[0];
        delegated.Identity.TenantId.Should().Be(ScopeId);
        delegated.Identity.AppId.Should().Be("default");
        delegated.Identity.Namespace.Should().Be("default");
        delegated.Identity.ServiceId.Should().Be(PublishedServiceId);
        delegated.EndpointId.Should().Be("chat");
        delegated.Input.Prompt.Should().Be("hello team");
        delegated.Input.SessionId.Should().Be("session-1");
        delegated.Input.Headers.Should().Contain("x-trace", "trace-1");
    }

    [Fact]
    public async Task InvokeAsync_ShouldRejectArchivedTeam()
    {
        var service = CreateService(
            new RecordingStaticGAgentStreamInvocationPort(),
            team: NewTeam(lifecycleStage: TeamLifecycleStageNames.Archived));

        var act = () => service.InvokeAsync(
            NewRequest(),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.TeamArchived);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRejectMissingEntryMember()
    {
        var service = CreateService(
            new RecordingStaticGAgentStreamInvocationPort(),
            team: NewTeam(entryMemberId: null));

        var act = () => service.InvokeAsync(
            NewRequest(),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotConfigured);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRejectEntryMemberFromAnotherTeam()
    {
        var service = CreateService(
            new RecordingStaticGAgentStreamInvocationPort(),
            member: NewMember(teamId: "other-team"));

        var act = () => service.InvokeAsync(
            NewRequest(),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberMismatch);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRejectEntryMemberThatIsNotReady()
    {
        var service = CreateService(
            new RecordingStaticGAgentStreamInvocationPort(),
            member: NewMember(lifecycleStage: MemberLifecycleStageNames.BuildReady));

        var act = () => service.InvokeAsync(
            NewRequest(),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<TeamEntryMemberResolutionException>()
            .Where(ex => ex.Code == TeamEntryMemberErrorCodes.EntryMemberNotReady);
    }

    private static StudioTeamGAgentStreamInvocationService CreateService(
        RecordingStaticGAgentStreamInvocationPort staticPort,
        StudioTeamSummaryResponse? team = null,
        StudioMemberDetailResponse? member = null)
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(team ?? NewTeam()),
            new MemberQueryPort(member ?? NewMember()));
        return new StudioTeamGAgentStreamInvocationService(resolver, staticPort);
    }

    private static StudioTeamGAgentStreamInvocationRequest NewRequest() =>
        new(
            ScopeId,
            TeamId,
            "chat",
            new StaticGAgentStreamInvocationInput("hello"));

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
            ImplementationKind: MemberImplementationKindNames.GAgent,
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

    private sealed class RecordingStaticGAgentStreamInvocationPort : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public List<StaticGAgentStreamInvocationRequest> Requests { get; } = [];

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var accepted = new StaticGAgentStreamAcceptedReceipt(
                new ServiceInvocationAcceptedReceipt
                {
                    RequestId = "cmd-1",
                    ServiceKey = "scope-1:default:default:published-service-1",
                    DeploymentId = "dep-1",
                    TargetActorId = "actor-1",
                    EndpointId = request.EndpointId,
                    CommandId = "cmd-1",
                    CorrelationId = "corr-1",
                },
                new GAgentDraftRunAcceptedReceipt("actor-1", "RoleGAgent", "cmd-1", "corr-1"));

            if (onAcceptedAsync != null)
                await onAcceptedAsync(accepted, ct);

            await emitAsync(
                new AGUIEvent
                {
                    RunStarted = new RunStartedEvent
                    {
                        ThreadId = "actor-1",
                        RunId = "cmd-1",
                    },
                },
                ct);

            return new StaticGAgentStreamInvocationResult(
                accepted,
                GAgentDraftRunStartError.None,
                GAgentDraftRunCompletionStatus.RunFinished,
                CompletionObserved: true);
        }
    }
}
