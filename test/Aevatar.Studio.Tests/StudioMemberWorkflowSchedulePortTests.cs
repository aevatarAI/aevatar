using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowSchedulePortTests
{
    [Fact]
    public async Task EnsureAsync_HappyPath_SchedulesExistingBoundWorkflowMember()
    {
        var memberService = new RecordingMemberService
        {
            Detail = CreateWorkflowMemberDetail(),
        };
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(scheduleService, memberService);

        var result = await ScheduleAsync(sut, Request("scope-1", "member-1") with
        {
            Prompt = "run digest",
            DisplayName = "Daily digest",
        });

        result.Success.Should().BeTrue();
        result.Status.Should().Be("accepted");
        result.ScopeId.Should().Be("scope-1");
        result.MemberId.Should().Be("member-1");
        result.ScheduleId.Should().Be(scheduleService.Configuration!.ScheduleId);
        result.PublishedServiceId.Should().Be("published-member-1");
        result.ObservatoryUrl.Should().Be("/workflow/observatory");

        memberService.GetScopeId.Should().Be("scope-1");
        memberService.GetMemberId.Should().Be("member-1");
        memberService.CreateCallCount.Should().Be(0);
        memberService.BindCallCount.Should().Be(0);

        scheduleService.EnsureCallCount.Should().Be(1);
        var configuration = scheduleService.Configuration!;
        configuration.DisplayName.Should().Be("Daily digest");
        configuration.CronExpression.Should().Be("0 9 * * *");
        configuration.Timezone.Should().Be("Asia/Shanghai");
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        configuration.Enabled.Should().BeTrue();
        configuration.Headers.Should().BeEmpty();

        var invocation = configuration.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be("scope-1");
        invocation.Identity.ServiceId.Should().Be("published-member-1");
        invocation.EndpointId.Should().Be("chat");
        var chat = invocation.Payload.Unpack<ChatRequestEvent>();
        chat.Prompt.Should().Be("run digest");
        chat.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task EnsureAsync_ThreadsCallerSubjectRefIntoDispatchAuth()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(scheduleService);

        await ScheduleAsync(sut, Request("scope-1", "member-1") with
        {
            CallerSubjectPlatform = " Lark ",
            CallerSubjectTenant = " tenant-1 ",
        });

        var auth = scheduleService.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.SenderNyxId!.Subject.Platform.Should().Be("Lark");
        auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-1");
        auth.SenderNyxId.Subject.ExternalUserId.Should().Be("sender-alpha");
        auth.SenderNyxId.Scope.Should().Be(ProvisionWorkflowCallerCredential.DefaultScope);
        auth.NyxId!.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.Sender);
        auth.Durable.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
        scheduleService.MutationContext.Should().BeEquivalentTo(new ScheduledDispatchMutationContext(
            "scope-1",
            new ScheduledServiceInvocationNyxIdSubjectRef("Lark", "tenant-1", "sender-alpha")));
    }

    [Fact]
    public async Task PreflightAsync_ShouldReturnTypedAuthorizationPlan()
    {
        var planner = new RecordingAuthorizationPlanner();
        var port = NewPort(new RecordingScheduleService(), planner: planner);

        var result = await port.PreflightAsync(Request("scope-1", "member-1"));

        result.Success.Should().BeTrue();
        result.Plan!.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        planner.Requests.Should().ContainSingle();
        planner.Requests[0].InvocationTarget.Studio.MemberId.Should().Be("member-1");
        planner.Requests[0].InvocationTarget.Studio.PublishedServiceId.Should().Be("published-member-1");
    }

    [Fact]
    public async Task CreateAsync_WhenAuthorizationFails_ShouldNotDispatch()
    {
        var scheduleService = new RecordingScheduleService();
        var planner = new RecordingAuthorizationPlanner
        {
            Result = ScheduledInvocationAuthorizationPlanResult.Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "snapshot_missing"),
        };
        var port = NewPort(scheduleService, planner: planner);

        var action = () => port.CreateAsync(Request("scope-1", "member-1"), "confirmed-digest");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("snapshot_missing");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenPermissionDigestChanged_ShouldNotDispatch()
    {
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(scheduleService);

        var action = () => port.ReauthorizeAsync(Request("scope-1", "member-1"), "stale-digest");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("authorization_plan_changed");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenJustAcceptedBindingRun_ShouldScheduleBeforeLastBindingMaterializes()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(
            scheduleService,
            new RecordingMemberService
            {
                Detail = CreateWorkflowMemberDetail(
                    hasBinding: false,
                    currentBindingRunStatus: StudioMemberBindingRunStatusNames.Accepted),
            });

        var result = await ScheduleAsync(sut, Request("scope-1", "member-1"));

        result.Success.Should().BeTrue();
        scheduleService.EnsureCallCount.Should().Be(1);
        scheduleService.Configuration!.Target.ServiceInvocation!.Identity.ServiceId.Should().Be("published-member-1");
    }

    [Fact]
    public async Task EnsureAsync_WhenBindingRunFailedAndNoLastBinding_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(
            scheduleService,
            new RecordingMemberService
            {
                Detail = CreateWorkflowMemberDetail(
                    hasBinding: false,
                    currentBindingRunStatus: StudioMemberBindingRunStatusNames.Failed),
            });

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' has no bound workflow*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenWorkflowMemberUnbound_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(
            scheduleService,
            new RecordingMemberService { Detail = CreateWorkflowMemberDetail(hasBinding: false) });

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' has no bound workflow*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenMemberIsNotWorkflow_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(
            scheduleService,
            new RecordingMemberService { Detail = CreateWorkflowMemberDetail(implementationKind: MemberImplementationKindNames.Script) });

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' is not a workflow member*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenBaseScheduleIdTombstoned_ShouldUseNextGeneration()
    {
        var scheduleService = new RecordingScheduleService { TombstonedAttempts = 1 };

        var result = await ScheduleAsync(NewPort(scheduleService), Request("scope-1", "member-1"));

        scheduleService.EnsureCallCount.Should().Be(2);
        var attemptedScheduleIds = scheduleService.Configurations.Select(static configuration => configuration.ScheduleId).ToArray();
        attemptedScheduleIds[1].Should().Be($"{attemptedScheduleIds[0]}.2");
        result.ScheduleId.Should().Be(attemptedScheduleIds[1]);
    }

    [Fact]
    public async Task EnsureAsync_ShouldUseDeterministicScheduleIdPerScopeAndMember()
    {
        var first = new RecordingScheduleService();
        var second = new RecordingScheduleService();
        var otherScope = new RecordingScheduleService();
        var otherMember = new RecordingScheduleService();

        await ScheduleAsync(NewPort(first), Request("scope-1", "member-1"));
        await ScheduleAsync(NewPort(second), Request("scope-1", "member-1"));
        await ScheduleAsync(NewPort(otherScope), Request("scope-2", "member-1"));
        await ScheduleAsync(NewPort(otherMember), Request("scope-1", "member-2"));

        var scheduleId = first.Configuration!.ScheduleId;
        second.Configuration!.ScheduleId.Should().Be(scheduleId);
        otherScope.Configuration!.ScheduleId.Should().NotBe(scheduleId);
        otherMember.Configuration!.ScheduleId.Should().NotBe(scheduleId);
        scheduleId.Should().StartWith("studio-member-workflow-");
        scheduleId.Should().MatchRegex("^[A-Za-z0-9._-]+$");
    }

    private static StudioMemberWorkflowScheduleRequest Request(string scopeId, string memberId) => new(
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            AuthenticatedOwner: new Aevatar.Studio.Application.Authorization.AuthenticatedNyxIdOwnerContext
            {
                Owner = new Aevatar.Studio.Application.Authorization.NyxIdCatalogOwnerIdentity
                {
                    Authority = "nyxid",
                    OwnerKind = Aevatar.Studio.Application.Authorization.NyxIdCatalogOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                SubjectPlatform = "lark",
                SubjectExternalUserId = "sender-alpha",
                VerifiedBindingId = "binding-alpha",
            },
            CredentialExpiresAtUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z"));

    private static StudioMemberWorkflowSchedulePort NewPort(
        RecordingScheduleService schedule,
        RecordingMemberService? memberService = null,
        IScheduledInvocationAuthorizationPlanner? planner = null) =>
        new(memberService ?? new RecordingMemberService { Detail = CreateWorkflowMemberDetail() }, schedule,
            planner ?? new RecordingAuthorizationPlanner());

    private static async Task<StudioMemberWorkflowScheduleResult> ScheduleAsync(
        StudioMemberWorkflowSchedulePort port,
        StudioMemberWorkflowScheduleRequest request)
    {
        var preflight = await port.PreflightAsync(request);
        preflight.Success.Should().BeTrue();
        return await port.CreateAsync(request, preflight.Plan!.PermissionDigest);
    }

    private static StudioMemberDetailResponse CreateWorkflowMemberDetail(
        string implementationKind = MemberImplementationKindNames.Workflow,
        bool hasBinding = true,
        string? currentBindingRunStatus = null) =>
        new(
            Summary: new StudioMemberSummaryResponse(
                MemberId: "member-1",
                ScopeId: "scope-1",
                DisplayName: "Member",
                Description: string.Empty,
                ImplementationKind: implementationKind,
                LifecycleStage: MemberLifecycleStageNames.BindReady,
                PublishedServiceId: "published-member-1",
                LastBoundRevisionId: hasBinding ? "rev-1" : null,
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
            ImplementationRef: null,
            LastBinding: hasBinding
                ? new StudioMemberBindingContractResponse(
                    PublishedServiceId: "published-member-1",
                    RevisionId: "rev-1",
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    BoundAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
                : null)
        {
            CurrentBindingRun = currentBindingRunStatus is null
                ? null
                : new StudioMemberBindingRunStatusResponse(
                    BindingRunId: "bind-1",
                    ScopeId: "scope-1",
                    MemberId: "member-1",
                    Status: currentBindingRunStatus,
                    StateVersion: 1,
                    UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
        };

    private sealed class RecordingMemberService : IStudioMemberService
    {
        public StudioMemberDetailResponse? Detail { get; init; }
        public string? GetScopeId { get; private set; }
        public string? GetMemberId { get; private set; }
        public int CreateCallCount { get; private set; }
        public int BindCallCount { get; private set; }

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            GetScopeId = scopeId;
            GetMemberId = memberId;
            return Task.FromResult(Detail ?? throw new StudioMemberNotFoundException(scopeId, memberId));
        }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            CreateCallCount++;
            throw new NotSupportedException();
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default)
        {
            BindCallCount++;
            throw new NotSupportedException();
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAuthorizationPlanner : IScheduledInvocationAuthorizationPlanner
    {
        public const string Digest = "permission-digest-alpha";
        public List<ScheduledInvocationAuthorizationRequest> Requests { get; } = [];
        public ScheduledInvocationAuthorizationPlanResult Result { get; init; } =
            ScheduledInvocationAuthorizationPlanResult.Succeeded(new ScheduledInvocationAuthorizationPlan
            {
                PermissionDigest = Digest,
            });

        public Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
            ScheduledInvocationAuthorizationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public int EnsureCallCount { get; private set; }
        public int TombstonedAttempts { get; init; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public ScheduledDispatchMutationContext? MutationContext { get; private set; }
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            EnsureCallCount++;
            Configuration = configuration;
            MutationContext = context;
            Configurations.Add(configuration);
            if (EnsureCallCount <= TombstonedAttempts)
                throw new ScheduledDispatchNotFoundException(configuration.ScheduleId);

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                configuration.ScheduleId,
                $"scheduled-dispatch:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd-1",
                CorrelationId: "corr-1",
                AckedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId, ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> EnableAsync(
            string scheduleId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> DisableAsync(
            string scheduleId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> DeleteAsync(
            string scheduleId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchDetail?> GetAsync(
            string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50, string? cursor = null, bool includeTotalCount = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression, string? timezone, int count, DateTimeOffset? fromUtc = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(
            string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
