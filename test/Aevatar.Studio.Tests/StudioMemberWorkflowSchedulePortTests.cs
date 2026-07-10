using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
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
        var sut = new StudioMemberWorkflowSchedulePort(memberService, scheduleService);

        var result = await sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1")
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
        var sut = new StudioMemberWorkflowSchedulePort(
            new RecordingMemberService { Detail = CreateWorkflowMemberDetail() },
            scheduleService);

        await sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: " owner-1 ")
        {
            CallerSubjectPlatform = " Lark ",
            CallerSubjectTenant = " tenant-1 ",
        });

        var auth = scheduleService.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.SenderNyxId!.Subject.Platform.Should().Be("Lark");
        auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-1");
        auth.SenderNyxId.Subject.ExternalUserId.Should().Be("owner-1");
        auth.SenderNyxId.Scope.Should().Be(ProvisionWorkflowCallerCredential.DefaultScope);
<<<<<<< HEAD
        auth.DurableCredentialReference.Should().BeNull();
=======
        auth.NyxId!.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.Sender);
        auth.Durable.Should().BeNull();
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
        auth.ScopeOwnerNyxId.Should().BeNull();
    }

    [Fact]
    public async Task EnsureAsync_WhenJustAcceptedBindingRun_ShouldScheduleBeforeLastBindingMaterializes()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = new StudioMemberWorkflowSchedulePort(
            new RecordingMemberService
            {
                Detail = CreateWorkflowMemberDetail(
                    hasBinding: false,
                    currentBindingRunStatus: StudioMemberBindingRunStatusNames.Accepted),
            },
            scheduleService);

        var result = await sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1"));

        result.Success.Should().BeTrue();
        scheduleService.EnsureCallCount.Should().Be(1);
        scheduleService.Configuration!.Target.ServiceInvocation!.Identity.ServiceId.Should().Be("published-member-1");
    }

    [Fact]
    public async Task EnsureAsync_WhenBindingRunFailedAndNoLastBinding_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = new StudioMemberWorkflowSchedulePort(
            new RecordingMemberService
            {
                Detail = CreateWorkflowMemberDetail(
                    hasBinding: false,
                    currentBindingRunStatus: StudioMemberBindingRunStatusNames.Failed),
            },
            scheduleService);

        var action = () => sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' has no bound workflow*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenWorkflowMemberUnbound_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = new StudioMemberWorkflowSchedulePort(
            new RecordingMemberService { Detail = CreateWorkflowMemberDetail(hasBinding: false) },
            scheduleService);

        var action = () => sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' has no bound workflow*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenMemberIsNotWorkflow_ShouldRejectBeforeScheduling()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = new StudioMemberWorkflowSchedulePort(
            new RecordingMemberService { Detail = CreateWorkflowMemberDetail(implementationKind: MemberImplementationKindNames.Script) },
            scheduleService);

        var action = () => sut.EnsureAsync(new StudioMemberWorkflowScheduleRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("member_id 'member-1' is not a workflow member*");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_WhenBaseScheduleIdTombstoned_ShouldUseNextGeneration()
    {
        var scheduleService = new RecordingScheduleService { TombstonedAttempts = 1 };

        var result = await NewPort(scheduleService).EnsureAsync(Request("scope-1", "member-1"));

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

        await NewPort(first).EnsureAsync(Request("scope-1", "member-1"));
        await NewPort(second).EnsureAsync(Request("scope-1", "member-1"));
        await NewPort(otherScope).EnsureAsync(Request("scope-2", "member-1"));
        await NewPort(otherMember).EnsureAsync(Request("scope-1", "member-2"));

        var scheduleId = first.Configuration!.ScheduleId;
        second.Configuration!.ScheduleId.Should().Be(scheduleId);
        otherScope.Configuration!.ScheduleId.Should().NotBe(scheduleId);
        otherMember.Configuration!.ScheduleId.Should().NotBe(scheduleId);
        scheduleId.Should().StartWith("studio-member-workflow-");
        scheduleId.Should().MatchRegex("^[A-Za-z0-9._-]+$");
    }

    private static StudioMemberWorkflowScheduleRequest Request(string scopeId, string memberId) =>
        new(
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            CallerSubjectExternalUserId: "owner-1");

    private static StudioMemberWorkflowSchedulePort NewPort(RecordingScheduleService schedule) =>
        new(new RecordingMemberService { Detail = CreateWorkflowMemberDetail() }, schedule);

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

    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public int EnsureCallCount { get; private set; }
        public int TombstonedAttempts { get; init; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration, CancellationToken ct = default)
        {
            EnsureCallCount++;
            Configuration = configuration;
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
            ScheduledDispatchConfiguration configuration, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId, ScheduledDispatchConfiguration configuration, CancellationToken ct = default) =>
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
