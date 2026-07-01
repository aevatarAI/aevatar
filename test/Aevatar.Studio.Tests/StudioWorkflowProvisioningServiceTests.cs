using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Unit tests for the one-call workflow provisioning service (C1, v2 async). The
/// service is a pure composition over
/// <see cref="Aevatar.Studio.Application.Studio.Abstractions.IStudioMemberService"/>
/// (create + bind) and <see cref="IScheduledDispatchApplicationService"/> (create
/// the scheduled-dispatch that produces the run). These tests pin the
/// orchestration contract for the NON-BLOCKING design:
/// <list type="bullet">
///   <item>create → bind → create-scheduled-dispatch, threading the scope
///   through every call;</item>
///   <item>the dispatch is a <see cref="ScheduledDispatchScheduleKind.Workflow"/>
///   service-invocation targeting the bound member's <c>chat</c> endpoint with the
///   caller prompt — the Workflow kind is what projects the caller token onto the
///   run;</item>
///   <item>the caller's NyxID subject reference is threaded into the dispatch
///   <see cref="ScheduledServiceInvocationAuth"/> (re-minted per fire, not a raw
///   token);</item>
///   <item>the bind is NEVER polled to completion — the service never calls
///   <c>GetBindingRunAsync</c>;</item>
///   <item>the response is "accepted" (202) carrying the schedule id + binding run
///   id + Observatory link.</item>
/// </list>
/// </summary>
public sealed class StudioWorkflowProvisioningServiceTests
{
    private const string ScopeId = "scope-1";
    private const string OtherScopeId = "scope-2";
    private const string MemberId = "member-1";
    private const string PublishedServiceId = "member-member-1";
    private const string BindingRunId = "bind-run-1";
    private const string ScheduleId = "schedule-xyz";

    private static ProvisionWorkflowCallerCredential Caller =>
        new(Platform: "nyxid", ExternalUserId: "user-42", Scope: "proxy", Tenant: "tenant-1");

    [Fact]
    public async Task ProvisionAsync_HappyPath_CreatesBindsAndSchedulesWithoutPollingBind()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go"));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.MemberId.Should().Be(MemberId);
        response.ScopeId.Should().Be(ScopeId);
        response.ScheduleId.Should().Be(ScheduleId);
        response.BindingRunId.Should().Be(BindingRunId);
        response.ObservatoryUrl.Should().Be("/workflow/observatory");

        // create → bind, carrying the caller scope.
        member.CreateScopeId.Should().Be(ScopeId);
        member.CreateRequest!.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        member.BindScopeId.Should().Be(ScopeId);
        member.BindRequest!.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Be("name: monitor");
        member.BindRequest.Workflow.WorkflowId.Should().NotBeNullOrWhiteSpace();

        // The bind is asynchronous; the service must NOT poll it to completion.
        member.GetBindingRunCallCount.Should().Be(0);

        // A Workflow-kind scheduled-dispatch was created targeting the bound member.
        schedule.Created.Should().BeTrue();
        var configuration = schedule.Configuration!;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        var invocation = configuration.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be(ScopeId);
        invocation.Identity.ServiceId.Should().Be(PublishedServiceId);
        invocation.EndpointId.Should().Be("chat");
        var chat = invocation.Payload.Unpack<ChatRequestEvent>();
        chat.Prompt.Should().Be("go");
        chat.ScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task ProvisionAsync_ThreadsCallerSubjectRefIntoDispatchAuth()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowCallerCredential(
                Platform: " Lark ", ExternalUserId: " ou-user-1 ", Scope: " proxy ", Tenant: " tenant-9 "),
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"));

        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.SenderNyxId!.Subject.Platform.Should().Be("Lark");
        auth.SenderNyxId.Subject.ExternalUserId.Should().Be("ou-user-1");
        auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-9");
        auth.SenderNyxId.Scope.Should().Be("proxy");
        auth.DurableSenderBearerToken.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_ThreadsForwardedCallerToken_AsDurableRunCredential_WhenNoIssuer()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"),
            callerBearerToken: CallerBearerToken);

        // One-shot demo (no caller cron) with no credential issuer: the forwarded
        // caller token is valid for the single near-future fire, so it is the run's
        // sole credential. The schedule carries EXACTLY ONE source — the subject ref
        // is NOT also attached (the validator admits only one, and the dispatch never
        // falls back to a subject ref once a token is present).
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.DurableSenderBearerToken.Should().Be(CallerBearerToken);
        auth.SenderNyxId.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_MintsDurableKey_AndThreadsItAsRunCredential_WhenIssuerWired()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"),
            callerBearerToken: CallerBearerToken);

        // The issuer minted a durable key under the caller's account, keyed by the
        // member id and scope, using the caller bearer token.
        issuer.IssueInvoked.Should().BeTrue();
        issuer.CallerBearerToken.Should().Be(CallerBearerToken);
        issuer.AgentId.Should().Be(MemberId);
        issuer.ScopeId.Should().Be(ScopeId);

        // The minted key (NOT the forwarded token) is the run's sole credential; a
        // long-lived key needs no subject-ref fallback, so none is attached.
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.DurableSenderBearerToken.Should().Be(MintedKey);
        auth.SenderNyxId.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_FallsBackToForwardedToken_WhenIssuerMintsNothing()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = null };
        var sut = NewService(member, schedule, issuer);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"),
            callerBearerToken: CallerBearerToken);

        issuer.IssueInvoked.Should().BeTrue();
        // The issuer minted nothing (e.g. no host slug configured); for a one-shot
        // demo the forwarded caller token is the sole credential so provisioning still
        // produces an authenticatable run.
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.DurableSenderBearerToken.Should().Be(CallerBearerToken);
        auth.SenderNyxId.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_NoDurableCredential_WhenNoBearerToken()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"));

        // No forwarded token → nothing to mint from and nothing to thread; the
        // subject ref is the schedule's sole credential (the dispatch re-mints a
        // fresh sender token from it on every fire).
        issuer.IssueInvoked.Should().BeFalse();
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.DurableSenderBearerToken.Should().BeNull();
        auth.SenderNyxId.Should().NotBeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_RecurringMonitor_WithForwardedTokenOnly_ShouldRejectBeforeCreatingSchedule()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = null };
        var sut = NewService(member, schedule, issuer);

        var act = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "0 8 * * *",
                Timezone: "Asia/Shanghai"),
            callerBearerToken: CallerBearerToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Recurring workflow schedules require a durable run credential;*");
        issuer.IssueInvoked.Should().BeTrue();
        schedule.Created.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_RecurringMonitor_WithoutBearerToken_ShouldRejectBeforeCreatingSchedule()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var act = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "0 8 * * *",
                Timezone: "Asia/Shanghai"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Recurring workflow schedules require a durable run credential;*");
        schedule.Created.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_RecurringMonitor_WithMintedKey_UsesDurableKey()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "0 8 * * *",
                Timezone: "Asia/Shanghai"),
            callerBearerToken: CallerBearerToken);

        // A minted durable key authenticates every fire with no re-mint dependency,
        // so a recurring monitor uses it directly as the sole source.
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.DurableSenderBearerToken.Should().Be(MintedKey);
        auth.SenderNyxId.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_WithBearerToken_StillReturnsAcceptedAndScheduleId_WithoutPollingBind()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go"),
            callerBearerToken: CallerBearerToken);

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.ScheduleId.Should().Be(ScheduleId);
        // Minting a durable credential must not turn the non-blocking flow into a
        // bind poll.
        member.GetBindingRunCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_DefaultsToOneShotCron_WhenNoCronSupplied()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule, out var time);
        // Pin a deterministic clock so the synthesized one-shot cron is stable.
        time.SetUtcNow(new DateTimeOffset(2026, 6, 19, 10, 30, 15, TimeSpan.Zero));

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go"));

        // now=10:30:15, +30s=10:30:45, rounded up to next whole minute = 10:31.
        // Fixed-minute one-shot cron: "minute hour day month *".
        schedule.Configuration!.CronExpression.Should().Be("31 10 19 6 *");
        schedule.Configuration.Timezone.Should().Be(ScheduledDispatchCalculator.DefaultTimezone);
    }

    [Fact]
    public async Task ProvisionAsync_UsesCallerCron_ForRecurringMonitor()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "*/15 * * * *",
                Timezone: "Asia/Shanghai"),
            callerBearerToken: CallerBearerToken);

        schedule.Configuration!.CronExpression.Should().Be("*/15 * * * *");
        schedule.Configuration.Timezone.Should().Be("Asia/Shanghai");
    }

    [Fact]
    public async Task ProvisionAsync_RunImmediatelyFalseWithoutCron_BindsButCreatesNoSchedule()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                RunImmediately: false));

        // Bind happened, but with nothing to fire there is no schedule and no run.
        member.BindScopeId.Should().Be(ScopeId);
        schedule.Created.Should().BeFalse();
        response.ScheduleId.Should().BeNull();
        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
    }

    [Fact]
    public async Task ProvisionAsync_RunImmediatelyFalseWithCron_StillCreatesRecurringSchedule()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var issuer = new RecordingRunCredentialIssuer { MintedKey = MintedKey };
        var sut = NewService(member, schedule, issuer);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                RunImmediately: false,
                Cron: "*/15 * * * *"),
            callerBearerToken: CallerBearerToken);

        schedule.Created.Should().BeTrue();
        schedule.Configuration!.CronExpression.Should().Be("*/15 * * * *");
        response.ScheduleId.Should().Be(ScheduleId);
    }

    [Fact]
    public async Task ProvisionAsync_ThreadsCallerScope_NotAmbient()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            OtherScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"));

        member.CreateScopeId.Should().Be(OtherScopeId);
        member.BindScopeId.Should().Be(OtherScopeId);
        var invocation = schedule.Configuration!.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be(OtherScopeId);
        invocation.Payload.Unpack<ChatRequestEvent>().ScopeId.Should().Be(OtherScopeId);
    }

    [Fact]
    public async Task ProvisionAsync_PropagatesScheduleCreateFailure()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService
        {
            ThrowOnCreate = new InvalidOperationException("cron is invalid"),
        };
        var sut = NewService(member, schedule);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("cron is invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProvisionAsync_RejectsMissingWorkflowYaml(string yaml)
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: yaml));

        await act.Should().ThrowAsync<InvalidOperationException>();
        member.CreateInvoked.Should().BeFalse();
        schedule.Created.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_RejectsMissingCallerSubject()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowCallerCredential(Platform: "", ExternalUserId: "", Scope: "proxy"),
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        schedule.Created.Should().BeFalse();
    }

    private const string CallerBearerToken = "caller-bearer-token";
    private const string MintedKey = "minted-durable-agent-key";

    /// <summary>
    /// The platform validator (<see cref="IScheduledDispatchApplicationService"/>
    /// CreateAsync → NormalizeServiceInvocationAuth) admits a schedule only when its
    /// auth carries EXACTLY ONE credential source. These unit tests drive a recording
    /// schedule service that does NOT run that validator, so this guard pins the
    /// invariant at the producer — the gap that let a two-source regression ship and
    /// fail live with "Exactly one service invocation credential source is required."
    /// </summary>
    private static void AssertExactlyOneCredentialSource(ScheduledServiceInvocationAuth? auth)
    {
        auth.Should().NotBeNull();
        var sources =
            (auth!.SenderNyxId != null ? 1 : 0) +
            (string.IsNullOrEmpty(auth.DurableSenderBearerToken) ? 0 : 1) +
            (auth.ScopeOwnerNyxId != null ? 1 : 0);
        sources.Should().Be(1, "a scheduled dispatch must carry exactly one credential source");
    }

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        IStudioRunCredentialIssuer? credentialIssuer = null) =>
        NewService(member, schedule, out _, credentialIssuer);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        out FakeTimeProvider time,
        IStudioRunCredentialIssuer? credentialIssuer = null)
    {
        time = new FakeTimeProvider();
        return new StudioWorkflowProvisioningService(member, schedule, credentialIssuer, time);
    }

    private static RecordingMemberService NewMemberService() =>
        new()
        {
            MemberId = MemberId,
            PublishedServiceId = PublishedServiceId,
            BindingRunId = BindingRunId,
        };

    /// <summary>
    /// Hand-rolled spy implementing only the members the provisioning service
    /// uses in the async flow: create + bind. <c>GetBindingRunAsync</c> records a
    /// call count and throws if invoked — the service must not poll the bind.
    /// </summary>
    private sealed class RecordingMemberService : Application.Studio.Abstractions.IStudioMemberService
    {
        public string MemberId { get; set; } = "member-1";
        public string PublishedServiceId { get; set; } = "member-member-1";
        public string BindingRunId { get; set; } = "bind-run-1";
        public string? TeamId { get; set; }

        public bool CreateInvoked { get; private set; }
        public string? CreateScopeId { get; private set; }
        public CreateStudioMemberRequest? CreateRequest { get; private set; }
        public string? BindScopeId { get; private set; }
        public UpdateStudioMemberBindingRequest? BindRequest { get; private set; }
        public int GetBindingRunCallCount { get; private set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            CreateInvoked = true;
            CreateScopeId = scopeId;
            CreateRequest = request;
            return Task.FromResult(new StudioMemberSummaryResponse(
                MemberId: MemberId,
                ScopeId: scopeId,
                DisplayName: request.DisplayName,
                Description: string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: PublishedServiceId,
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                TeamId = TeamId,
            });
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default)
        {
            BindScopeId = scopeId;
            BindRequest = request;
            return Task.FromResult(new StudioMemberBindingAcceptedResponse(
                Status: StudioMemberBindingRunStatusNames.Accepted,
                BindingRunId: BindingRunId,
                ScopeId: scopeId,
                MemberId: memberId));
        }

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default)
        {
            GetBindingRunCallCount++;
            throw new InvalidOperationException(
                "The async provisioning flow must not poll the binding run to completion.");
        }

        // ---- Unused members (the provisioning service never calls these) ----

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
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
    }

    /// <summary>
    /// Records the scheduled-dispatch configuration the provisioning service
    /// builds and returns a mutation receipt with a fixed schedule id. Only
    /// <see cref="CreateAsync"/> is exercised; the rest throw.
    /// </summary>
    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public string ScheduleId { get; set; } = "schedule-xyz";
        public Exception? ThrowOnCreate { get; set; }
        public bool Created { get; private set; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration, CancellationToken ct = default)
        {
            if (ThrowOnCreate != null)
                throw ThrowOnCreate;

            Created = true;
            Configuration = configuration;
            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                ScheduleId,
                $"scheduled-dispatch:{ScheduleId}",
                Accepted: true,
                CommandId: "cmd-1",
                CorrelationId: "corr-1",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        // ---- Unused members ----

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
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

    /// <summary>
    /// Records the durable run-credential mint request and returns a fixed minted
    /// key (or null to simulate "could not mint", e.g. no host slug configured).
    /// </summary>
    private sealed class RecordingRunCredentialIssuer : IStudioRunCredentialIssuer
    {
        public string? MintedKey { get; set; }
        public bool IssueInvoked { get; private set; }
        public string? CallerBearerToken { get; private set; }
        public string? AgentId { get; private set; }
        public string? ScopeId { get; private set; }

        public Task<string?> IssueDurableRunCredentialAsync(
            string callerBearerToken, string agentId, string scopeId, CancellationToken ct = default)
        {
            IssueInvoked = true;
            CallerBearerToken = callerBearerToken;
            AgentId = agentId;
            ScopeId = scopeId;
            return Task.FromResult(MintedKey);
        }
    }

    /// <summary>
    /// Manual-set time provider so the synthesized one-shot cron is deterministic.
    /// The service only reads <see cref="GetUtcNow"/> (it never sleeps), so no
    /// timer is needed and no polling-wait magic numbers exist.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }
}
