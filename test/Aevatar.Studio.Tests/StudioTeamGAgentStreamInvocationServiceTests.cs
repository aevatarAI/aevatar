using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.AGUI.Contracts;
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
        var readinessPort = new ReadyScopeBindingReadinessQueryPort();
        var service = CreateService(staticPort, readinessPort: readinessPort);
        var emitted = new List<AGUIEvent>();
        StudioTeamStreamInvocationAcceptedReceipt? accepted = null;

        var result = await service.InvokeAsync(
            new StudioTeamStreamInvocationRequest(
                ScopeId,
                TeamId,
                "chat",
                new StudioTeamStreamInvocationInput(
                    Prompt: "hello team",
                    PreferredActorId: "actor-preferred",
                    SessionId: "session-1",
                    RevisionId: "rev-1",
                    Headers: new Dictionary<string, string>
                    {
                        ["x-trace"] = "trace-1",
                    },
                    InputParts:
                    [
                        new StudioTeamStreamInvocationInputPart("text", Text: "hello"),
                        new StudioTeamStreamInvocationInputPart(
                            "image",
                            DataBase64: "aW1hZ2U=",
                            MediaType: "image/png",
                            Name: "image.png"),
                    ],
                    Timeout: TimeSpan.FromSeconds(15))),
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

        result.AcceptedReceipt.Should().NotBeNull();
        result.AcceptedReceipt!.RunId.Should().Be("cmd-1");
        result.AcceptedReceipt.ThreadId.Should().Be("actor-1");
        result.AcceptedReceipt.CorrelationId.Should().Be("corr-1");
        result.StartError.Should().Be(nameof(GAgentDraftRunStartError.None));
        result.CompletionStatus.Should().Be(nameof(GAgentDraftRunCompletionStatus.RunFinished));
        result.CompletionObserved.Should().BeTrue();
        accepted.Should().BeEquivalentTo(result.AcceptedReceipt);
        emitted.Should().ContainSingle()
            .Which.RunStarted.RunId.Should().Be("cmd-1");

        staticPort.Requests.Should().ContainSingle();
        var delegated = staticPort.Requests[0];
        delegated.Identity.TenantId.Should().Be(ScopeId);
        delegated.Identity.AppId.Should().Be(ScopeServiceIdentityDefaults.ServiceAppId);
        delegated.Identity.Namespace.Should().Be(ScopeServiceIdentityDefaults.ServiceNamespace);
        delegated.Identity.ServiceId.Should().Be(PublishedServiceId);
        delegated.EndpointId.Should().Be("chat");
        readinessPort.LastRequest.Should().NotBeNull();
        readinessPort.LastRequest!.ExpectedEndpointIds.Should().BeEquivalentTo(
            ["chat"],
            options => options.WithStrictOrdering());
        delegated.Input.Prompt.Should().Be("hello team");
        delegated.Input.PreferredActorId.Should().Be("actor-preferred");
        delegated.Input.SessionId.Should().Be("session-1");
        delegated.Input.RevisionId.Should().Be("rev-1");
        delegated.Input.Headers.Should().Contain("x-trace", "trace-1");
        delegated.Input.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        delegated.Input.InputParts.Should().NotBeNull();
        delegated.Input.InputParts!.Should().HaveCount(2);
        delegated.Input.InputParts[0].Kind.Should().Be(GAgentDraftRunInputPartKind.Text);
        delegated.Input.InputParts[0].Text.Should().Be("hello");
        delegated.Input.InputParts[1].Kind.Should().Be(GAgentDraftRunInputPartKind.Image);
        delegated.Input.InputParts[1].DataBase64.Should().Be("aW1hZ2U=");
        delegated.Input.InputParts[1].MediaType.Should().Be("image/png");
        delegated.Input.InputParts[1].Name.Should().Be("image.png");
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
        StudioMemberDetailResponse? member = null,
        ReadyScopeBindingReadinessQueryPort? readinessPort = null)
    {
        var resolver = new StudioTeamEntryMemberResolver(
            new TeamQueryPort(team ?? NewTeam()),
            new MemberQueryPort(member ?? NewMember()),
            readinessPort ?? new ReadyScopeBindingReadinessQueryPort());
        return new StudioTeamGAgentStreamInvocationService(
            resolver,
            staticPort);
    }

    private sealed class ReadyScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        public ScopeBindingReadinessRequest? LastRequest { get; private set; }

        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ScopeBindingReadinessSnapshot(
                request.ScopeId,
                request.ServiceId,
                ScopeBindingReadinessStatus.Ready,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: true,
                RevisionId: request.ExpectedRevisionId ?? "rev-1",
                DeploymentId: "dep-1",
                ObservedAtUtc: DateTimeOffset.UtcNow));
        }
    }

    private static StudioTeamStreamInvocationRequest NewRequest() =>
        new(
            ScopeId,
            TeamId,
            "chat",
            new StudioTeamStreamInvocationInput("hello"));

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
