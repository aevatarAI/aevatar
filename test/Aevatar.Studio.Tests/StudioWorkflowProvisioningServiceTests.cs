using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Unit tests for the one-call workflow provisioning service (C1, v2 async). The
/// service is a pure composition over
/// <see cref="Aevatar.Studio.Application.Studio.Abstractions.IStudioMemberService"/>
/// (create + bind) and <see cref="IScheduledDispatchApplicationService"/> (ensure
/// the scheduled-dispatch that produces the run). These tests pin the
/// orchestration contract for the NON-BLOCKING design:
/// <list type="bullet">
///   <item>the workflow YAML is validated synchronously through the binding
///   admission service BEFORE anything is created — invalid YAML provisions nothing;</item>
///   <item>validate → create → bind → ensure-scheduled-dispatch, threading the
///   scope through every call;</item>
///   <item>member id, workflow id and schedule id derive deterministically from
///   (scope, display name), so retries converge on the same resources instead of
///   accumulating garbage;</item>
///   <item>the dispatch is a <see cref="ScheduledDispatchScheduleKind.Workflow"/>
///   service-invocation targeting the bound member's <c>chat</c> endpoint with the
///   caller prompt — the Workflow kind is what projects the caller token onto the
///   run;</item>
///   <item>the caller's NyxID subject reference is threaded into the dispatch
///   <see cref="ScheduledServiceInvocationAuth"/> (re-minted per fire, not a raw
///   token);</item>
///   <item>the bind is NEVER polled to completion — the service never calls
///   <c>GetBindingRunAsync</c>;</item>
///   <item>the response is "accepted" (202) carrying the workflow id, schedule
///   stage, optional schedule id, binding run id, and Observatory link.</item>
/// </list>
/// </summary>
public sealed class StudioWorkflowProvisioningServiceTests
{
    private const string ScopeId = "scope-1";
    private const string OtherScopeId = "scope-2";
    private const string TeamId = "team-alpha";
    private const string OtherTeamId = "team-beta";
    private const string MemberId = "member-1";
    private const string PublishedServiceId = "member-member-1";
    private const string BindingRunId = "bind-run-1";
    private const string ScheduleId = "schedule-xyz";

    private static ProvisionWorkflowCallerCredential Caller =>
        new(Platform: "nyxid", ExternalUserId: "user-42", Scope: "proxy", Tenant: "tenant-1");

    [Fact]
    public async Task ProvisionAsync_RejectsMissingTeamId_BeforeAdmissionOrProvisioning()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = new StudioWorkflowCapabilityAdmissionTestService();
        var sut = NewService(member, schedule, admission);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("teamId is required.");
        admission.Requests.Should().BeEmpty();
        member.CreateInvoked.Should().BeFalse();
        member.BindScopeId.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_TeamOwnedProvision_CreatesMemberInTeamAndReturnsStudioUrl()
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
                Prompt: "go")
            {
                TeamId = TeamId,
            });

        member.CreateRequest!.TeamId.Should().Be(TeamId);
        response.TeamId.Should().Be(TeamId);
        response.StudioUrl.Should()
            .Be($"/scopes/{ScopeId}/teams/{TeamId}/members/{MemberId}/workflow");
    }

    [Fact]
    public async Task ProvisionAsync_WhenCapabilityAdmissionFails_ShouldMutateNothing()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new InvalidOperationException("external capability is not ready"));
        var sut = NewService(member, schedule, admission);

        var act = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor")
            {
                TeamId = TeamId,
                CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                    "caller-alpha",
                    "runtime-caller-credential"),
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("external capability is not ready");
        var request = admission.Requests.Should().ContainSingle().Which;
        request.Access.ScopeId.Should().Be(ScopeId);
        request.Access.CallerId.Should().Be("caller-alpha");
        request.Access.NyxIdCallerBearerToken.Should().Be("runtime-caller-credential");
        request.SourceKind.Should().Be("studio_workflow_provisioning");
        request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_WhenScheduledPlanIsPersisted_ShouldRevalidateAsDurable()
    {
        const string workflowYaml = "name: monitor";
        var persistedPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [],
            []);
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = new StudioWorkflowCapabilityAdmissionTestService();
        var sut = NewService(member, schedule, admission);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", workflowYaml)
            {
                TeamId = TeamId,
                CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                    "caller-alpha",
                    existingPlan: persistedPlan),
            });

        admission.Requests.Should().BeEmpty();
        admission.PersistedRequests.Should().ContainSingle()
            .Which.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        member.BindRequest!.CapabilityAdmission!.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
    }

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
                Prompt: "go")
            {
                TeamId = TeamId,
            });

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.MemberId.Should().Be(MemberId);
        response.ScopeId.Should().Be(ScopeId);
        response.ScheduleId.Should().Be(ScheduleId);
        response.BindingRunId.Should().Be(BindingRunId);
        response.ObservatoryUrl.Should().Be("/workflow/observatory");

        // create → bind, carrying the caller scope and Team ownership.
        member.CreateScopeId.Should().Be(ScopeId);
        member.CreateRequest!.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        member.CreateRequest.TeamId.Should().Be(TeamId);
        member.BindScopeId.Should().Be(ScopeId);
        member.BindRequest!.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Be("name: monitor");
        member.BindRequest.Workflow.WorkflowId.Should().NotBeNullOrWhiteSpace();

        // The bind is asynchronous; the service must NOT poll it to completion.
        member.GetBindingRunCallCount.Should().Be(0);

        // A Workflow-kind scheduled-dispatch was created targeting the bound member.
        schedule.Ensured.Should().BeTrue();
        var configuration = schedule.Configuration!;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        var invocation = configuration.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be(ScopeId);
        invocation.Identity.ServiceId.Should().Be(PublishedServiceId);
        invocation.EndpointId.Should().Be("chat");
        var chat = invocation.Payload.Unpack<ChatRequestEvent>();
        chat.Prompt.Should().Be("go");
        chat.ScopeId.Should().Be(ScopeId);
        schedule.MutationContext.Should().BeNull();
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p")
            {
                TeamId = TeamId,
            });

        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.SenderNyxId!.Subject.Platform.Should().Be("Lark");
        auth.SenderNyxId.Subject.ExternalUserId.Should().Be("ou-user-1");
        auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-9");
        auth.SenderNyxId.Scope.Should().Be("proxy");
        auth.NyxId!.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.Sender);
        auth.Durable.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_ShouldNotUseBodyCallerAsAuthenticatedScheduleMutationContext()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowCallerCredential(
                Platform: " nyxid-body ",
                ExternalUserId: " body-user-42 ",
                Scope: " sender-proxy ",
                Tenant: " body-tenant "),
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p")
            {
                TeamId = TeamId,
            });

        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth!;
        auth.SenderNyxId!.Subject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef("nyxid-body", "body-tenant", "body-user-42"));
        auth.SenderNyxId.Scope.Should().Be("sender-proxy");
        schedule.MutationContext.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_UsesSubjectRefAsOnlyCredentialSource()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p")
            {
                TeamId = TeamId,
            });

        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.Durable.Should().BeNull();
        auth.SenderNyxId.Should().NotBeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_RecurringMonitor_UsesSubjectRef()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "0 8 * * *",
                Timezone: "Asia/Shanghai")
            {
                TeamId = TeamId,
            });

        // The re-mintable subject reference is the only schedule credential.
        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth;
        auth!.SenderNyxId.Should().NotBeNull();
        auth.Durable.Should().BeNull();
        AssertExactlyOneCredentialSource(auth);
    }

    [Fact]
    public async Task ProvisionAsync_ReturnsAcceptedAndScheduleId_WithoutPollingBind()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
            });

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.ScheduleId.Should().Be(ScheduleId);
        member.GetBindingRunCallCount.Should().Be(0);
        schedule.Configuration!.Target.ServiceInvocation!.Auth!.Durable.Should().BeNull();
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
            });

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
        var sut = NewService(member, schedule);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                Cron: "*/15 * * * *",
                Timezone: "Asia/Shanghai")
            {
                TeamId = TeamId,
            });

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
                RunImmediately: false)
            {
                TeamId = TeamId,
            });

        // Bind happened, but with nothing to fire there is no schedule and no run.
        member.BindScopeId.Should().Be(ScopeId);
        schedule.Ensured.Should().BeFalse();
        response.ScheduleId.Should().BeNull();
        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
    }

    [Fact]
    public async Task ProvisionAsync_RunImmediatelyFalseWithCron_StillCreatesRecurringSchedule()
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
                RunImmediately: false,
                Cron: "*/15 * * * *")
            {
                TeamId = TeamId,
            });

        schedule.Ensured.Should().BeTrue();
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p")
            {
                TeamId = TeamId,
            });

        member.CreateScopeId.Should().Be(OtherScopeId);
        member.BindScopeId.Should().Be(OtherScopeId);
        var invocation = schedule.Configuration!.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be(OtherScopeId);
        invocation.Payload.Unpack<ChatRequestEvent>().ScopeId.Should().Be(OtherScopeId);
    }

    [Fact]
    public async Task ProvisionAsync_InvalidWorkflowYaml_FailsFastAndProvisionsNothing()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new InvalidOperationException("Unsupported workflow YAML root field 'version'."));
        var sut = NewService(member, schedule, admission);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "version: \"1.0\"\ninputs: {}\nname: monitor")
            {
                TeamId = TeamId,
            });

        // The admission error travels to the caller so an authoring agent can
        // repair the YAML — and nothing was provisioned: no member, no bind, no
        // schedule to fire against a member that never bound.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Unsupported workflow YAML root field 'version'");
        member.CreateInvoked.Should().BeFalse();
        member.BindScopeId.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_AdmitsYamlThroughUnifiedService_BeforeProvisioning()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = new StudioWorkflowCapabilityAdmissionTestService();
        var sut = NewService(member, schedule, admission);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
            });

        admission.Requests.Should().ContainSingle()
            .Which.WorkflowYaml.Should().Be("name: monitor");
    }

    [Fact]
    public async Task ProvisionAsync_SameScopeAndDisplayName_ConvergesOnSameResourceIds()
    {
        var firstMember = NewMemberService();
        var firstSchedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var first = NewService(firstMember, firstSchedule);
        var secondMember = NewMemberService();
        var secondSchedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var second = NewService(secondMember, secondSchedule);
        var request = new ProvisionWorkflowRequest(
            DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
        {
            TeamId = TeamId,
        };

        await first.ProvisionAsync(ScopeId, Caller, request);
        await second.ProvisionAsync(ScopeId, Caller, request);

        // Same (scope, display name) → same member id, workflow id and schedule
        // id: a retry re-binds and re-schedules the same resources instead of
        // leaving a fresh member + enabled schedule per attempt.
        firstMember.CreateRequest!.MemberId.Should().NotBeNullOrWhiteSpace();
        firstMember.CreateRequest.MemberId.Should().Be(secondMember.CreateRequest!.MemberId);
        firstMember.CreateRequest.MemberId.Should().MatchRegex(
            StudioMemberInputLimits.MemberIdPattern.ToString());
        firstMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            secondMember.BindRequest!.Workflow!.WorkflowId);
        firstSchedule.Configuration!.ScheduleId.Should().Be($"provision-{PublishedServiceId}");
        firstSchedule.Configuration.ScheduleId.Should().Be(secondSchedule.Configuration!.ScheduleId);
    }

    [Fact]
    public async Task ProvisionAsync_SameIdempotencyKeyAcrossDisplayNames_ConvergesOnSameResourceIds()
    {
        var firstMember = NewMemberService();
        var firstSchedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var first = NewService(firstMember, firstSchedule);
        var fallbackMember = NewMemberService();
        var fallbackSchedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var fallback = NewService(fallbackMember, fallbackSchedule);

        await first.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Weekly Report", WorkflowYaml: "name: weekly\n")
            {
                TeamId = TeamId,
                IdempotencyKey = "chat-turn-alpha",
            });
        await fallback.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Weekly Report Fallback", WorkflowYaml: "name: weekly\n")
            {
                TeamId = TeamId,
                IdempotencyKey = "chat-turn-alpha",
            });

        firstMember.CreateRequest!.MemberId.Should().NotBeNullOrWhiteSpace();
        firstMember.CreateRequest.MemberId.Should().Be(fallbackMember.CreateRequest!.MemberId);
        firstMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            fallbackMember.BindRequest!.Workflow!.WorkflowId);
    }

    [Fact]
    public async Task ProvisionAsync_DifferentDisplayNameOrScope_DerivesDistinctMemberIds()
    {
        var baseline = NewMemberService();
        var renamed = NewMemberService();
        var otherScope = NewMemberService();

        await NewService(baseline, new RecordingScheduleService()).ProvisionAsync(
            ScopeId, Caller, new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });
        await NewService(renamed, new RecordingScheduleService()).ProvisionAsync(
            ScopeId, Caller, new ProvisionWorkflowRequest(DisplayName: "Other", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });
        await NewService(otherScope, new RecordingScheduleService()).ProvisionAsync(
            OtherScopeId, Caller, new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });

        baseline.CreateRequest!.MemberId.Should().NotBe(renamed.CreateRequest!.MemberId);
        baseline.CreateRequest.MemberId.Should().NotBe(otherScope.CreateRequest!.MemberId);
    }

    [Fact]
    public async Task ProvisionAsync_WhenMemberAlreadyExists_ReusesItWithoutRecreating()
    {
        // A member renamed after provisioning must not be re-created — the
        // deterministic id is the identity, the display name a mutable label.
        var member = NewMemberService();
        member.ExistingDetail = new StudioMemberDetailResponse(
            Summary: new StudioMemberSummaryResponse(
                MemberId: MemberId,
                ScopeId: ScopeId,
                DisplayName: "Renamed by user",
                Description: string.Empty,
                ImplementationKind: MemberImplementationKindNames.Workflow,
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: PublishedServiceId,
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                TeamId = TeamId,
            },
            ImplementationRef: null,
            LastBinding: null);
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
            });

        member.GetCallCount.Should().Be(1);
        member.CreateInvoked.Should().BeFalse();
        member.BindScopeId.Should().Be(ScopeId);
        response.MemberId.Should().Be(MemberId);
        schedule.Configuration!.Target.ServiceInvocation!.Identity.ServiceId.Should().Be(PublishedServiceId);
    }

    [Fact]
    public async Task ProvisionAsync_SameDisplayNameInDifferentTeams_DerivesDistinctMemberIds()
    {
        var baseline = NewMemberService();
        var otherTeam = NewMemberService();

        await NewService(baseline, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });
        await NewService(otherTeam, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = OtherTeamId,
            });

        baseline.CreateRequest!.MemberId.Should().NotBe(otherTeam.CreateRequest!.MemberId);
    }

    [Fact]
    public async Task ProvisionAsync_WhenScheduleGenerationTombstoned_AdvancesToNextGeneration()
    {
        // A user-deleted schedule is a permanent tombstone; re-provisioning the
        // same display name must converge on the next schedule generation
        // instead of silently ensuring against the tombstone forever.
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        schedule.TombstonedScheduleIds.Add($"provision-{PublishedServiceId}");
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
            });

        schedule.Ensured.Should().BeTrue();
        schedule.Configuration!.ScheduleId.Should().Be($"provision-{PublishedServiceId}.2");
        response.ScheduleId.Should().Be(ScheduleId);
    }

    [Fact]
    public async Task ProvisionAsync_WhenScheduleFailsAfterBind_ShouldReturnStagefulReceiptForCreatedResources()
    {
        var member = NewMemberService();
        member.MemberId = "m-alpha";
        member.PublishedServiceId = "svc-alpha";
        var schedule = new RecordingScheduleService
        {
            ThrowOnEnsure = new InvalidOperationException("owner_llm_authorization_evidence_not_found"),
        };
        var sut = NewService(member, schedule);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.MemberId.Should().Be("m-alpha");
        response.WorkflowId.Should().NotBeNullOrWhiteSpace();
        response.WorkflowId.Should().NotBe(response.MemberId);
        response.ScheduleId.Should().BeNull();
        response.ProvisioningStage.Should().Be(WorkflowScheduleProvisioningStageNames.ScheduleBlocked);
        response.ScheduleStatus.Should().Be(WorkflowScheduleProvisioningScheduleStatusNames.Blocked);
        response.StageFailure.Should().BeEquivalentTo(new WorkflowScheduleProvisioningStageFailure(
            Stage: WorkflowScheduleProvisioningStageNames.ScheduleBlocked,
            Code: "owner_llm_authorization_evidence_not_found",
            Message: "owner_llm_authorization_evidence_not_found"));

        member.CreateInvoked.Should().BeTrue();
        member.BindRequest.Should().NotBeNull();
        schedule.Ensured.Should().BeFalse();
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: yaml)
            {
                TeamId = TeamId,
            });

        await act.Should().ThrowAsync<InvalidOperationException>();
        member.CreateInvoked.Should().BeFalse();
        schedule.Ensured.Should().BeFalse();
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
            });

        await act.Should().ThrowAsync<InvalidOperationException>();
        schedule.Ensured.Should().BeFalse();
    }

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
        var sources = auth!.Source == null ? 0 : 1;
        sources.Should().Be(1, "a scheduled dispatch must carry exactly one credential source");
    }

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule) =>
        NewService(member, schedule, new StudioWorkflowCapabilityAdmissionTestService(), out _);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        StudioWorkflowCapabilityAdmissionTestService admission) =>
        NewService(member, schedule, admission, out _);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        out FakeTimeProvider time) =>
        NewService(member, schedule, new StudioWorkflowCapabilityAdmissionTestService(), out time);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        StudioWorkflowCapabilityAdmissionTestService admission,
        out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new StudioWorkflowProvisioningService(
            member,
            schedule,
            admission,
            time);
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

        /// <summary>
        /// When set, <see cref="GetAsync"/> returns this member detail (the
        /// "already provisioned" case); when null, it throws
        /// <see cref="StudioMemberNotFoundException"/> like the real readmodel
        /// query for an absent member.
        /// </summary>
        public StudioMemberDetailResponse? ExistingDetail { get; set; }
        public int GetCallCount { get; private set; }

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

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            GetCallCount++;
            return ExistingDetail != null
                ? Task.FromResult(ExistingDetail)
                : throw new StudioMemberNotFoundException(scopeId, memberId);
        }

        // ---- Unused members (the provisioning service never calls these) ----

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
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

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Records the scheduled-dispatch configuration the provisioning service
    /// builds and returns a mutation receipt with a fixed schedule id. Only
    /// <see cref="EnsureAsync"/> is exercised (the idempotent upsert that lets
    /// retries converge on one schedule); the rest throw — including
    /// <see cref="CreateAsync"/>, which would mint a new schedule per retry.
    /// </summary>
    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public string ScheduleId { get; set; } = "schedule-xyz";
        public Exception? ThrowOnEnsure { get; set; }
        public bool Ensured { get; private set; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public ScheduledDispatchMutationContext? MutationContext { get; private set; }

        /// <summary>
        /// Schedule ids that behave like delete tombstones: ensuring them throws
        /// the typed not-found the platform surfaces for a deleted schedule.
        /// </summary>
        public HashSet<string> TombstonedScheduleIds { get; } = new(StringComparer.Ordinal);

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            if (ThrowOnEnsure != null)
                throw ThrowOnEnsure;
            if (TombstonedScheduleIds.Contains(configuration.ScheduleId))
                throw new ScheduledDispatchNotFoundException(configuration.ScheduleId);

            Ensured = true;
            Configuration = configuration;
            MutationContext = context;
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

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException(
                "Provisioning must use EnsureAsync so retries converge on one schedule.");

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
