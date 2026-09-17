using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Unit tests for the one-call workflow provisioning service (C1, v2 async). The
/// service is a pure composition over
/// <see cref="Aevatar.Studio.Application.Studio.Abstractions.IStudioMemberService"/>,
/// <see cref="IStudioMemberWorkflowBindingPort"/> and
/// <see cref="IStudioMemberWorkflowSchedulePort"/>. These tests pin the
/// orchestration contract for the NON-BLOCKING design:
/// <list type="bullet">
///   <item>the workflow YAML is validated synchronously through the binding
///   admission service BEFORE anything is created — invalid YAML provisions nothing;</item>
///   <item>validate → create → bind → accept actor-owned schedule provisioning,
///   threading the scope through every call;</item>
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
///   <item>the response is "accepted" (202) carrying the provisioning id, binding
///   run id, and Observatory link; schedule id may still be absent.</item>
/// </list>
/// </summary>
public sealed class StudioWorkflowProvisioningServiceTests
{
    private const string ScopeId = "scope-1";
    private const string OtherScopeId = "scope-2";
    private const string TeamId = "team-alpha";
    private const string OtherTeamId = "team-beta";
    private const string MemberId = "m-alpha";
    private const string WorkflowId = "wf-alpha";
    private const string PublishedServiceId = "svc-alpha";
    private const string RevisionId = "rev-alpha";
    private const string BindingRunId = "bind-run-1";
    private const string ScheduleId = "schedule-xyz";

    private static ProvisionWorkflowCallerCredential Caller =>
        new(Platform: "nyxid", ExternalUserId: "user-42", Scope: "proxy", Tenant: "tenant-1");

    private static (string WorkflowId, string RevisionId) ProvisionIdentity(
        string scopeId,
        string teamId,
        string displayName,
        string workflowYaml = StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Durable)
    {
        var key = StudioWorkflowProvisioningService.BuildProvisionKey(scopeId, teamId, displayName);
        var workflowId = $"workflow-{key}";
        return (
            workflowId,
            StudioWorkflowProvisioningService.BuildProvisionRevisionId(
                key,
                workflowId,
                workflowYaml,
                executionMode));
    }

    [Fact]
    public async Task ProvisionAsync_WithMatchingExplicitConfirmation_ShouldAdmitBeforeStudioMutation()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var sut = NewService(member, schedule, admission);
        var identity = ProvisionIdentity(ScopeId, TeamId, "Monitor");

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                        identity.WorkflowId,
                        identity.RevisionId)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        var admissionRequest = admission.Requests.Should().ContainSingle().Which;
        admissionRequest.Access.CallerId.Should().Be("caller-alpha");
        admissionRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        admissionRequest.Access.NyxIdOrganizationBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        admissionRequest.ExplicitRequestConfirmations.Should().ContainSingle();
        var plan = member.BindRequest!.Workflow!.CapabilityAdmissionPlan;
        plan.Should().NotBeNull();
        plan!.InvocationAdmissions.Should().ContainSingle()
            .Which.NyxIdExplicitRequestGrant.GrantorOwnerSubject.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerId);
        member.BindRequest.CapabilityAdmission!.NyxIdCallerCredential.Should().BeNull();
        member.BindRequest.CapabilityAdmission.NyxIdOrganizationBearerToken.Should().BeNull();
        member.BindRequest.CapabilityAdmission.ExplicitRequestConfirmations.Should().BeEmpty();
        plan.ToString().Should().NotContain(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        plan.ToString().Should().NotContain(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        schedule.LastCreateRequest!.AcceptedBinding.Should().BeEquivalentTo(
            new StudioMemberWorkflowAcceptedBindingContext(
                TeamId,
                PublishedServiceId,
                WorkflowId,
                RevisionId));
    }

    [Fact]
    public async Task ProvisionAsync_WhenBindingReceiptOmitsResolvedIdentity_ShouldUseRequestedIdentity()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var bindingPort = new RecordingBindingPort(member, null, null);
        var schedulePort = new RecordingWorkflowSchedulePort(schedule);
        var sut = new StudioWorkflowProvisioningService(
            member,
            bindingPort,
            ImmediateScheduleCommand(schedulePort),
            new StudioWorkflowCapabilityAdmissionTestService());

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        bindingPort.LastResult!.WorkflowId.Should().BeNull();
        bindingPort.LastResult.RevisionId.Should().BeNull();
        schedulePort.LastPreflightRequest!.AcceptedBinding.Should().BeEquivalentTo(
            new StudioMemberWorkflowAcceptedBindingContext(
                TeamId,
                PublishedServiceId,
                bindingPort.LastRequest!.WorkflowId!,
                bindingPort.LastRequest.RevisionId!));
    }

    [Fact]
    public async Task ProvisionAsync_WithUnboundExplicitConfirmation_ShouldBindMintedWorkflowIdentity()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var sut = NewService(member, schedule, admission);
        var identity = ProvisionIdentity(ScopeId, TeamId, "Monitor");

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        var provisionalConfirmation = admission.Requests.Should().ContainSingle().Which
            .ExplicitRequestConfirmations.Should().ContainSingle().Which;
        provisionalConfirmation.WorkflowId.Should().Be(identity.WorkflowId);
        provisionalConfirmation.RevisionId.Should().Be(identity.RevisionId);
        member.BindRequest!.RevisionId.Should().NotBe(identity.RevisionId);
        var finalPlan = member.BindRequest.Workflow!.CapabilityAdmissionPlan!;
        finalPlan.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(member.BindRequest.RevisionId);
        finalPlan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
                new Dictionary<string, string>(),
                identity.WorkflowId,
                member.BindRequest.RevisionId));
        finalPlan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(finalPlan));
        member.CreateInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_UnboundExplicitConfirmation_WhenCatalogSnapshotChanges_ShouldAdvanceRevision()
    {
        var firstMember = NewMemberService();
        var secondMember = NewMemberService();
        var firstAdmission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(sourceVersion: 23);
        var secondAdmission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(sourceVersion: 24);
        var request = new ProvisionWorkflowRequest(
            "Monitor",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            TeamId = TeamId,
            AuthenticatedOwner = TestAuthenticatedOwner(),
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                ExternalCapabilityExecutionMode.Durable),
        };

        await NewService(firstMember, new RecordingScheduleService(), firstAdmission)
            .ProvisionAsync(ScopeId, Caller, request);
        await NewService(secondMember, new RecordingScheduleService(), secondAdmission)
            .ProvisionAsync(ScopeId, Caller, request);

        firstMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            secondMember.BindRequest!.Workflow!.WorkflowId);
        firstMember.BindRequest.RevisionId.Should().NotBe(secondMember.BindRequest.RevisionId);
        firstAdmission.Requests.Should().ContainSingle();
        secondAdmission.Requests.Should().ContainSingle();
        firstMember.BindRequest.Workflow.CapabilityAdmissionPlan!.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(firstMember.BindRequest.RevisionId);
        secondMember.BindRequest.Workflow.CapabilityAdmissionPlan!.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(secondMember.BindRequest.RevisionId);
    }

    [Fact]
    public async Task ProvisionAsync_UnboundExplicitConfirmation_WhenSourceWindowChanges_ShouldAdvanceRevision()
    {
        var firstMember = NewMemberService();
        var secondMember = NewMemberService();
        var firstObservedAt = new DateTimeOffset(2026, 7, 30, 7, 59, 0, TimeSpan.Zero);
        var firstAdmission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
            sourceObservedAt: firstObservedAt);
        var secondAdmission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
            sourceObservedAt: firstObservedAt.AddMinutes(1));
        var request = new ProvisionWorkflowRequest(
            "Monitor",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            TeamId = TeamId,
            AuthenticatedOwner = TestAuthenticatedOwner(),
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                ExternalCapabilityExecutionMode.Durable),
        };

        await NewService(firstMember, new RecordingScheduleService(), firstAdmission)
            .ProvisionAsync(ScopeId, Caller, request);
        await NewService(secondMember, new RecordingScheduleService(), secondAdmission)
            .ProvisionAsync(ScopeId, Caller, request);

        firstMember.BindRequest!.RevisionId.Should().NotBe(secondMember.BindRequest!.RevisionId);
        firstAdmission.Requests.Should().ContainSingle();
        secondAdmission.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ProvisionAsync_WithExplicitConfirmationBoundToAnotherWorkflow_ShouldMutateNothing()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var sut = NewService(member, schedule, admission);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                        "workflow-other",
                        "revision-other")],
                    ExternalCapabilityExecutionMode.Durable),
            });

        var exception = await action.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.SafeBlockerCode.Should()
            .Be("NYXID_EXPLICIT_REQUEST_CONFIRMATION_BINDING_MISMATCH");
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenDurableCatalogIsMissing_ShouldPrepareExactGrantBeforeMutation()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var durableAttempts = 0;
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
                    ++durableAttempts == 1)
                {
                    throw DurableAuthorizationUnavailable(capability);
                }
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        admission.Requests.Select(static request => request.ExplicitRequestGrantMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Durable);
        refresh.Owner.Should().BeEquivalentTo(owner.Owner);
        refresh.BearerToken.Should().Be("runtime-caller-credential");
        refresh.Request!.RequiredServices.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-alpha");
        visibility.Owner.Should().BeEquivalentTo(owner.Owner);
        visibility.RequiredStateVersion.Should().Be(31);
        member.CreateInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_WhenRealAdmissionNeedsCatalogPreparation_ShouldRefreshAndReadmitDurable()
    {
        var catalogReady = false;
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
            durableCatalogReady: () => catalogReady);
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31))
        {
            OnRefresh = () => catalogReady = true,
        };
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = StudioExplicitRequestAdmissionTestKit.CallerId,
            },
            "nyxid",
            string.Empty,
            StudioExplicitRequestAdmissionTestKit.CallerId,
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        admission.Requests.Select(static request => request.ExplicitRequestGrantMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Durable);
        refresh.Request!.RequiredServices.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-alpha");
        member.BindRequest!.Workflow!.CapabilityAdmissionPlan!.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
        admission.Requests[^1].RevisionId.Should().NotBe(member.BindRequest.RevisionId);
        member.BindRequest.Workflow.CapabilityAdmissionPlan.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(member.BindRequest.RevisionId);
        schedule.Ensured.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_WhenTargetedRefreshPreservesStaleOwnerWindow_ShouldReadmitExactGrant()
    {
        var catalogReady = false;
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
            durableCatalogReady: () => catalogReady);
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31))
        {
            OnRefresh = () => catalogReady = true,
        };
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Stale,
            31,
            31,
            "nyxid_catalog_snapshot_stale"));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = StudioExplicitRequestAdmissionTestKit.CallerId,
            },
            "nyxid",
            string.Empty,
            StudioExplicitRequestAdmissionTestKit.CallerId,
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        visibility.RequiredStateVersion.Should().Be(31);
        admission.Requests[^1].RevisionId.Should().NotBe(member.BindRequest!.RevisionId);
        member.BindRequest.Workflow!.CapabilityAdmissionPlan!.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(member.BindRequest.RevisionId);
        member.CreateInvoked.Should().BeTrue();
        schedule.Ensured.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_WhenTargetedRefreshDoesNotProveGrant_ShouldFailClosedAfterReadmission()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
                    throw DurableAuthorizationUnavailable(capability);
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Stale,
            31,
            31,
            "nyxid_catalog_snapshot_stale"));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenRefreshedCatalogIsNotVisible_ShouldReturnPendingWithoutMutation()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
                    throw DurableAuthorizationUnavailable(capability);
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending,
            31,
            30,
            "catalog_projection_pending"));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        var exception = await action.Should()
            .ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        exception.Which.RequiredStateVersion.Should().Be(31);
        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive);
        refresh.Request!.RequiredServices.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-alpha");
        visibility.RequiredStateVersion.Should().Be(31);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenRequiredRouteIsUnresolved_ShouldFailBeforeMutationWithRepairTarget()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
                    throw DurableAuthorizationUnavailable(capability);
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                StudioMemberAutomationCatalogRouteUnresolvedException.NyxIdFailureCode));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        var exception = await action.Should()
            .ThrowAsync<StudioMemberAutomationCatalogRouteUnresolvedException>();
        exception.Which.RequiredUserServiceIds.Should().Equal("usvc-alpha");
        refresh.Request!.RequiredServices.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-alpha");
        visibility.RequiredStateVersion.Should().Be(0);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenCatalogAndAnotherRequirementAreMissing_ShouldNotRefreshOrMutate()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
                    throw DurableAuthorizationUnavailable(capability, includeAdditionalBlocker: true);
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable);
        refresh.Request.Should().BeNull();
        visibility.RequiredStateVersion.Should().Be(0);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenInteractivePlanDoesNotAllowDurable_ShouldNotRefreshOrMutate()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        capability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Remove(
            ExternalCapabilityExecutionMode.Durable);
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(capability),
            OnAdmit = request =>
            {
                if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
                    throw DurableAuthorizationUnavailable(capability);
            },
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "caller-alpha",
            "binding-alpha");
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(string.Empty, string.Empty)],
                    ExternalCapabilityExecutionMode.Durable),
            });

        await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        admission.Requests.Select(static request => request.ExecutionMode).Should().Equal(
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Interactive);
        refresh.Request.Should().BeNull();
        visibility.RequiredStateVersion.Should().Be(0);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WithUnknownAdmittedCapability_ShouldFailClosed()
    {
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(new ExternalWorkflowCapabilityRef()),
        };
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(NewMemberService(), schedule, admission);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor") { TeamId = TeamId });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*capability*");
        schedule.PreflightRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_WithKnownAndUnknownAdmittedCapabilities_ShouldFailClosed()
    {
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            AdmissionPlan = CapabilityPlan(
                new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserService = new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = "usvc-published-alpha",
                    },
                },
                new ExternalWorkflowCapabilityRef()),
        };
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(NewMemberService(), schedule, admission);

        var action = () => sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor") { TeamId = TeamId });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*capability*");
        schedule.PreflightRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing", "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED")]
    [InlineData("unknown", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("duplicate", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("stale_digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("stale_risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    public async Task ProvisionAsync_WithInvalidExplicitConfirmation_ShouldMutateNothing(
        string scenario,
        string expectedCode)
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var sut = NewService(member, schedule, admission);
        var identity = ProvisionIdentity("scope-studio-alpha", "team-alpha", "Monitor");

        var action = () => sut.ProvisionAsync(
            "scope-studio-alpha",
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = "team-alpha",
                AuthenticatedOwner = TestAuthenticatedOwner(),
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    StudioExplicitRequestAdmissionTestKit.Confirmations(
                        scenario,
                        identity.WorkflowId,
                        identity.RevisionId),
                    ExternalCapabilityExecutionMode.Durable),
            });

        var exception = await action.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle()
            .Which.Code.Should().Be(expectedCode);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
        schedule.PreflightRequests.Should().BeEmpty();
        schedule.LastCreateRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisionAsync_WithExistingPlan_ShouldUseCredentialAwareAdmissionPath(
        bool includeCallerCredential)
    {
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var identity = ProvisionIdentity("scope-studio-alpha", "team-alpha", "Monitor");
        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-studio-alpha",
                StudioExplicitRequestAdmissionTestKit.CallerId,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    StudioExplicitRequestAdmissionTestKit.CallerBearer),
                StudioExplicitRequestAdmissionTestKit.OrganizationBearer),
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
            new Dictionary<string, string>(),
            "test_prepare_plan",
            ExternalCapabilityExecutionMode.Durable,
            [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                identity.WorkflowId,
                identity.RevisionId)],
            workflowId: identity.WorkflowId,
            revisionId: identity.RevisionId));
        admission.Requests.Clear();
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule, admission);

        await sut.ProvisionAsync(
            "scope-studio-alpha",
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = "team-alpha",
                AuthenticatedOwner = TestAuthenticatedOwner(),
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    existingPlan: plan,
                    executionMode: ExternalCapabilityExecutionMode.Durable,
                    includeCallerCredential: includeCallerCredential),
            });

        admission.Requests.Should().BeEmpty();
        admission.RefreshRequests.Should().HaveCount(includeCallerCredential ? 1 : 0);
        admission.PersistedRequests.Should().HaveCount(includeCallerCredential ? 0 : 1);
        member.BindRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WithExistingPlanAndExpiredDurableCatalog_ShouldRefreshAndContinue()
    {
        var catalogReady = true;
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
            durableCatalogReady: () => catalogReady);
        var identity = ProvisionIdentity("scope-studio-alpha", "team-alpha", "Monitor");
        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-studio-alpha",
                StudioExplicitRequestAdmissionTestKit.CallerId,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    StudioExplicitRequestAdmissionTestKit.CallerBearer),
                StudioExplicitRequestAdmissionTestKit.OrganizationBearer),
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
            new Dictionary<string, string>(),
            "test_prepare_plan",
            ExternalCapabilityExecutionMode.Durable,
            [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                identity.WorkflowId,
                identity.RevisionId)],
            workflowId: identity.WorkflowId,
            revisionId: identity.RevisionId));
        catalogReady = false;
        admission.Requests.Clear();
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31))
        {
            OnRefresh = () => catalogReady = true,
        };
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        await sut.ProvisionAsync(
            "scope-studio-alpha",
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = "team-alpha",
                AuthenticatedOwner = TestAuthenticatedOwner(
                    ownerSubject: StudioExplicitRequestAdmissionTestKit.CallerId),
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    existingPlan: plan,
                    executionMode: ExternalCapabilityExecutionMode.Durable),
            });

        admission.RefreshRequests.Should().HaveCount(2);
        refresh.Owner.Should().BeEquivalentTo(TestAuthenticatedOwner(
            ownerSubject: StudioExplicitRequestAdmissionTestKit.CallerId).Owner);
        refresh.BearerToken.Should().Be("runtime-caller-credential");
        refresh.Request!.RequiredServices.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-alpha");
        visibility.RequiredStateVersion.Should().Be(31);
        member.BindRequest.Should().NotBeNull();
        member.BindRequest!.RevisionId.Should().Be(identity.RevisionId);
        schedule.Ensured.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_WithExistingPlanAndMixedCatalogBlockers_ShouldFailClosedWithoutRefresh()
    {
        var capability = DurableExplicitRequestCapability("usvc-alpha");
        var existingPlan = CapabilityPlan(capability);
        var admission = new StudioWorkflowCapabilityAdmissionTestService
        {
            OnRefresh = _ => throw DurableAuthorizationUnavailable(
                capability,
                includeAdditionalBlocker: true),
        };
        var refresh = new RecordingCatalogRefreshPort(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(31));
        var visibility = new RecordingCatalogVisibilityPort(new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Ready,
            31,
            31,
            string.Empty));
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule),
            admission,
            refresh,
            visibility);

        var action = () => sut.ProvisionAsync(
            "scope-studio-alpha",
            Caller,
            new ProvisionWorkflowRequest("Monitor", StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = "team-alpha",
                AuthenticatedOwner = TestAuthenticatedOwner(
                    ownerSubject: StudioExplicitRequestAdmissionTestKit.CallerId),
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    existingPlan: existingPlan,
                    executionMode: ExternalCapabilityExecutionMode.Durable),
            });

        await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        admission.RefreshRequests.Should().ContainSingle();
        refresh.Request.Should().BeNull();
        visibility.RequiredStateVersion.Should().Be(0);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();
        member.BindRequest.Should().BeNull();
        schedule.Ensured.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisionAsync_WithPlanReturnedByPrepare_ShouldReuseItsBoundRevisionIdentity(
        bool includeCallerCredential)
    {
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule, admission);
        var provisionalIdentity = ProvisionIdentity(
            "scope-studio-alpha",
            "team-alpha",
            "Monitor");
        var prepareRequest = new ProvisionWorkflowRequest(
            "Monitor",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            TeamId = "team-alpha",
            AuthenticatedOwner = TestAuthenticatedOwner(),
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                    string.Empty,
                    string.Empty)],
                ExternalCapabilityExecutionMode.Durable),
        };

        var preparation = await sut.PrepareAsync(
            "scope-studio-alpha",
            Caller,
            prepareRequest);

        preparation.RevisionId.Should().NotBe(provisionalIdentity.RevisionId);
        preparation.CapabilityAdmissionPlan.InvocationAdmissions.Should()
            .ContainSingle().Which.NyxIdExplicitRequestGrant.RevisionId.Should()
            .Be(preparation.RevisionId);
        member.GetCallCount.Should().Be(0);
        member.CreateInvoked.Should().BeFalse();

        await sut.ProvisionAsync(
            "scope-studio-alpha",
            Caller,
            new ProvisionWorkflowRequest(
                "Monitor",
                StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
            {
                TeamId = "team-alpha",
                AuthenticatedOwner = TestAuthenticatedOwner(),
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    existingPlan: preparation.CapabilityAdmissionPlan,
                    executionMode: ExternalCapabilityExecutionMode.Durable,
                    includeCallerCredential: includeCallerCredential),
            });

        member.BindRequest.Should().NotBeNull();
        member.BindRequest!.RevisionId.Should().Be(preparation.RevisionId);
        admission.RefreshRequests.Should().HaveCount(includeCallerCredential ? 1 : 0);
        admission.PersistedRequests.Should().HaveCount(includeCallerCredential ? 0 : 1);
    }

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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
                ProvisioningBearerToken = "runtime-caller-credential",
                CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                    "caller-alpha",
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                        "runtime-caller-credential")),
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("external capability is not ready");
        var request = admission.Requests.Should().ContainSingle().Which;
        request.Access.ScopeId.Should().Be(ScopeId);
        request.Access.CallerId.Should().Be("caller-alpha");
        request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("runtime-caller-credential");
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
                ProvisioningBearerToken = "runtime-caller-credential",
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
        var sut = NewService(member, schedule, out var bindingPort, out var schedulePort);

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.MemberId.Should().Be("m-alpha");
        response.ScopeId.Should().Be(ScopeId);
        response.ScheduleId.Should().Be(ScheduleId);
        response.BindingRunId.Should().Be(BindingRunId);
        response.ObservatoryUrl.Should().Be("/admin#/observatory");

        // create → bind, carrying the caller scope and Team ownership.
        member.CreateScopeId.Should().Be(ScopeId);
        new[] { "m-alpha", "wf-alpha", "svc-alpha", "rev-alpha" }
            .Should().NotContain(member.CreateRequest!.MemberId);
        member.CreateRequest!.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        member.CreateRequest.TeamId.Should().Be(TeamId);
        member.LastCreateResult!.MemberId.Should().Be("m-alpha");
        member.LastCreateResult.PublishedServiceId.Should().Be("svc-alpha");
        member.BindScopeId.Should().Be(ScopeId);
        member.BindMemberId.Should().Be("m-alpha");
        member.BindRequest!.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Be("name: monitor");
        member.BindRequest.Workflow.WorkflowId.Should().NotBeNullOrWhiteSpace();
        member.BindRequest.RevisionId.Should().NotBeNullOrWhiteSpace();
        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.MemberId.Should().Be("m-alpha");
        bindingPort.LastRequest.WorkflowId.Should().Be(member.BindRequest.Workflow.WorkflowId);
        bindingPort.LastRequest.RevisionId.Should().Be(member.BindRequest.RevisionId);
        new[] { "m-alpha", "wf-alpha", "svc-alpha", "rev-alpha" }
            .Should().NotContain(bindingPort.LastRequest.WorkflowId);
        new[] { "m-alpha", "wf-alpha", "svc-alpha", "rev-alpha" }
            .Should().NotContain(bindingPort.LastRequest.RevisionId);
        bindingPort.LastResult!.MemberId.Should().Be("m-alpha");
        bindingPort.LastResult.WorkflowId.Should().Be("wf-alpha");
        bindingPort.LastResult.RevisionId.Should().Be("rev-alpha");

        // The bind is asynchronous; the service must NOT poll it to completion.
        member.GetBindingRunCallCount.Should().Be(0);

        // The immediate test command port executes the accepted intent and creates
        // a Workflow-kind scheduled-dispatch targeting the bound member.
        schedule.Ensured.Should().BeTrue();
        var configuration = schedule.Configuration!;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        var invocation = configuration.Target.ServiceInvocation!;
        invocation.Identity.TenantId.Should().Be(ScopeId);
        invocation.Identity.ServiceId.Should().Be("svc-alpha");
        invocation.EndpointId.Should().Be("chat");
        var chat = invocation.Payload.Unpack<ChatRequestEvent>();
        chat.Prompt.Should().Be("go");
        chat.ScopeId.Should().Be(ScopeId);
        var owner = schedule.MutationContext!.TeamAutomationOwner;
        owner.Should().NotBeNull();
        owner!.ScopeId.Should().Be(ScopeId);
        owner.TeamId.Should().Be(TeamId);
        owner.MemberId.Should().Be("m-alpha");
        var preflightRequest = schedule.PreflightRequests.Should().ContainSingle().Which;
        preflightRequest.Should().BeSameAs(schedulePort.LastPreflightRequest);
        preflightRequest.MemberId.Should().Be("m-alpha");
        preflightRequest.AcceptedBinding.Should().BeEquivalentTo(
            new StudioMemberWorkflowAcceptedBindingContext(
                TeamId,
                PublishedServiceId,
                bindingPort.LastResult.WorkflowId!,
                bindingPort.LastResult.RevisionId!));
        schedulePort.LastCreateRequest.Should().BeSameAs(schedule.LastCreateRequest);
        schedulePort.LastResult!.MemberId.Should().Be("m-alpha");
        schedulePort.LastResult.PublishedServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task ProvisionAsync_ProductionCommandPort_ShouldReturnPendingWithoutExecutingScheduleWrite()
    {
        var member = NewMemberService();
        var bindingPort = new RecordingBindingPort(member, WorkflowId, RevisionId);
        var scheduleCommand = new RecordingScheduleProvisioningCommandPort();
        var sut = new StudioWorkflowProvisioningService(
            member,
            bindingPort,
            scheduleCommand,
            new StudioWorkflowCapabilityAdmissionTestService());

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
                ProvisioningBearerToken = "must-not-enter-intent",
            });

        response.ScheduleId.Should().BeNull();
        response.ScheduleProvisioningId.Should().Be(scheduleCommand.Intent!.ProvisioningId);
        response.ScheduleProvisioningStatus.Should().Be(
            StudioWorkflowScheduleProvisioningStatusNames.PendingBinding);
        scheduleCommand.Intent.BindingRunId.Should().Be(BindingRunId);
        scheduleCommand.Intent.RevisionId.Should().Be(RevisionId);
        scheduleCommand.Intent.AuthenticatedOwner.VerifiedBindingId.Should().Be("binding-alpha");
        scheduleCommand.Intent.ToString().Should().NotContain("must-not-enter-intent");
        member.GetBindingRunCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WithAcceptanceInput_ShouldRenderClonedSnapshotIntoSchedulePrompt()
    {
        var member = NewMemberService();
        var bindingPort = new RecordingBindingPort(member, WorkflowId, RevisionId);
        var scheduleCommand = new RecordingScheduleProvisioningCommandPort();
        var sut = new StudioWorkflowProvisioningService(
            member,
            bindingPort,
            scheduleCommand,
            new StudioWorkflowCapabilityAdmissionTestService());
        var sourceInput = new Struct
        {
            Fields =
            {
                ["dry_run"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                ["limit"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(5),
            },
        };
        var request = new ProvisionWorkflowRequest("Monitor", "name: monitor")
        {
            TeamId = TeamId,
            AuthenticatedOwner = TestAuthenticatedOwner(),
            AcceptanceInput = sourceInput,
        };

        sourceInput.Fields["dry_run"] = Google.Protobuf.WellKnownTypes.Value.ForBool(false);
        request.AcceptanceInput!.Fields["limit"] =
            Google.Protobuf.WellKnownTypes.Value.ForNumber(99);

        await sut.ProvisionAsync(ScopeId, Caller, request);

        var expectedInput = new Struct
        {
            Fields =
            {
                ["dry_run"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                ["limit"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(5),
            },
        };
        request.AcceptanceInput.Should().BeEquivalentTo(expectedInput);
        scheduleCommand.Intent!.Prompt.Should().Be(
            Google.Protobuf.JsonFormatter.Default.Format(expectedInput));
    }

    [Fact]
    public async Task ProvisionAsync_DeliveryRetry_ShouldReplayCurrentIntentAndAdvanceNextAttempt()
    {
        var member = NewMemberService();
        var bindingPort = new RecordingBindingPort(member, WorkflowId, RevisionId);
        var scheduleCommand = new RecordingScheduleProvisioningCommandPort();
        var sut = new StudioWorkflowProvisioningService(
            member,
            bindingPort,
            scheduleCommand,
            new StudioWorkflowCapabilityAdmissionTestService());
        var attemptOne = new ProvisionWorkflowRequest(
            "Workflow Alpha [delivery-alpha]",
            "name: monitor",
            Prompt: string.Empty)
        {
            TeamId = TeamId,
            AuthenticatedOwner = TestAuthenticatedOwner(),
            ScheduleOperationId = " installation-alpha:provision:a1 ",
            ScheduleIdempotencyKey = " publish-alpha ",
        };

        var first = await sut.ProvisionAsync(ScopeId, Caller, attemptOne);
        var replay = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            attemptOne with { ScheduleOperationId = "installation-alpha:provision:a1" });
        var retry = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            attemptOne with { ScheduleOperationId = "installation-alpha:provision:a2" });

        scheduleCommand.Intents.Should().HaveCount(3);
        scheduleCommand.Intents[0].ScheduleOperationId.Should().Be("installation-alpha:provision:a1");
        scheduleCommand.Intents[1].ProvisioningId.Should().Be(scheduleCommand.Intents[0].ProvisioningId);
        scheduleCommand.Intents[2].ProvisioningId.Should().NotBe(scheduleCommand.Intents[0].ProvisioningId);
        scheduleCommand.Intents.Select(static intent => intent.ScheduleIdempotencyKey)
            .Should().OnlyContain(key => key == "publish-alpha");
        first.ScheduleProvisioningId.Should().Be(replay.ScheduleProvisioningId);
        retry.ScheduleProvisioningId.Should().NotBe(first.ScheduleProvisioningId);
    }

    [Fact]
    public async Task ProvisionAsync_ThreadsAuthenticatedOwnerSubjectRefIntoDispatchAuth()
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
                AuthenticatedOwner = TestAuthenticatedOwner("Lark", "tenant-9", "ou-user-1"),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
                ProvisioningBearerToken = "runtime-caller-credential",
            });

        var auth = schedule.Configuration!.Target.ServiceInvocation!.Auth!;
        auth.SenderNyxId!.Subject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef("nyxid", string.Empty, "owner-alpha"));
        auth.SenderNyxId.Scope.Should().Be("proxy");
        var owner = schedule.MutationContext!.TeamAutomationOwner;
        owner.Should().NotBeNull();
        owner!.ScopeId.Should().Be(ScopeId);
        owner.TeamId.Should().Be(TeamId);
        owner.MemberId.Should().Be(MemberId);
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        response.ScheduleId.Should().Be(ScheduleId);
        member.GetBindingRunCallCount.Should().Be(0);
        schedule.Configuration!.Target.ServiceInvocation!.Auth!.Durable.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_DefaultsToFirstClassOneShot_WhenNoCronSupplied()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule, out var time);
        time.SetUtcNow(new DateTimeOffset(2026, 6, 19, 10, 30, 15, TimeSpan.Zero));

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        schedule.Configuration!.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
        schedule.Configuration.OneShotFireAt.Should()
            .Be(new DateTimeOffset(2026, 6, 19, 10, 30, 45, TimeSpan.Zero));
        schedule.Configuration.CronExpression.Should().BeEmpty();
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        schedule.Configuration!.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.RecurringCron);
        schedule.Configuration.OneShotFireAt.Should().BeNull();
        schedule.Configuration.CronExpression.Should().Be("*/15 * * * *");
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        schedule.Ensured.Should().BeTrue();
        schedule.Configuration!.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.RecurringCron);
        schedule.Configuration.OneShotFireAt.Should().BeNull();
        schedule.Configuration.CronExpression.Should().Be("*/15 * * * *");
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
            AuthenticatedOwner = TestAuthenticatedOwner(),
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
        firstMember.BindRequest.RevisionId.Should().Be(secondMember.BindRequest!.RevisionId);
        firstSchedule.Configuration!.ScheduleId.Should().Be($"provision-{PublishedServiceId}");
        firstSchedule.Configuration.ScheduleId.Should().Be(secondSchedule.Configuration!.ScheduleId);
    }

    [Fact]
    public async Task ProvisionAsync_ChangedWorkflowSpec_KeepsWorkflowIdentityAndAdvancesRevision()
    {
        var originalMember = NewMemberService();
        var changedMember = NewMemberService();

        await NewService(originalMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor\nsteps: []")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });
        await NewService(changedMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor\nsteps:\n  - id: changed")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        originalMember.CreateRequest!.MemberId.Should().Be(changedMember.CreateRequest!.MemberId);
        originalMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            changedMember.BindRequest!.Workflow!.WorkflowId);
        originalMember.BindRequest.RevisionId.Should().NotBe(changedMember.BindRequest.RevisionId);
    }

    [Fact]
    public async Task ProvisionAsync_ChangedInlineWorkflowSpec_KeepsWorkflowIdentityAndAdvancesRevision()
    {
        var originalMember = NewMemberService();
        var changedMember = NewMemberService();
        const string rootYaml = "name: root\nsteps: []";

        await NewService(originalMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", rootYaml, RunImmediately: false)
            {
                TeamId = TeamId,
                InlineWorkflowYamls = new Dictionary<string, string>
                {
                    ["child"] = "name: child\nsteps: []",
                },
            });
        await NewService(changedMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", rootYaml, RunImmediately: false)
            {
                TeamId = TeamId,
                InlineWorkflowYamls = new Dictionary<string, string>
                {
                    ["child"] = "name: child\nsteps:\n  - id: changed\n    type: assign",
                },
            });

        originalMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            changedMember.BindRequest!.Workflow!.WorkflowId);
        originalMember.BindRequest.RevisionId.Should().NotBe(changedMember.BindRequest.RevisionId);
        originalMember.BindRequest.Workflow.WorkflowYamls.Should().HaveCount(2);
        changedMember.BindRequest.Workflow.WorkflowYamls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProvisionAsync_InlineWorkflowYaml_ShouldUseOneCanonicalBundleForAdmissionAndBinding()
    {
        var member = NewMemberService();
        var sut = NewService(
            member,
            new RecordingScheduleService(),
            out var bindingPort,
            out _);
        const string rootYaml = "name: root\nsteps: []\n";
        const string childYaml = "name: child\nsteps: []\n";

        await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", rootYaml, RunImmediately: false)
            {
                TeamId = TeamId,
                InlineWorkflowYamls = new Dictionary<string, string>
                {
                    ["child"] = childYaml,
                },
            });

        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.WorkflowYaml.Should().Be(rootYaml.Trim());
        bindingPort.LastRequest.InlineWorkflowYamls.Should()
            .Contain("child", childYaml.Trim());
    }

    [Fact]
    public async Task ProvisionAsync_ChangedExecutionMode_AdvancesRevision()
    {
        var scheduledMember = NewMemberService();
        var bindOnlyMember = NewMemberService();
        const string yaml = "name: monitor\nsteps: []";

        await NewService(scheduledMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", yaml)
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });
        await NewService(bindOnlyMember, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", yaml, RunImmediately: false)
            {
                TeamId = TeamId,
            });

        scheduledMember.BindRequest!.Workflow!.WorkflowId.Should().Be(
            bindOnlyMember.BindRequest!.Workflow!.WorkflowId);
        scheduledMember.BindRequest.RevisionId.Should().NotBe(bindOnlyMember.BindRequest.RevisionId);
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });
        await NewService(renamed, new RecordingScheduleService()).ProvisionAsync(
            ScopeId, Caller, new ProvisionWorkflowRequest(DisplayName: "Other", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });
        await NewService(otherScope, new RecordingScheduleService()).ProvisionAsync(
            OtherScopeId, Caller, new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });
        await NewService(otherTeam, new RecordingScheduleService()).ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = OtherTeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
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
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        schedule.Ensured.Should().BeTrue();
        schedule.Configuration!.ScheduleId.Should().Be($"provision-{PublishedServiceId}.2");
        response.ScheduleId.Should().Be(ScheduleId);
    }

    [Fact]
    public async Task ProvisionAsync_WhenLiveScheduleExists_ShouldReplaceIt()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
        var sut = NewService(member, schedule, out _, out var schedulePort);
        schedulePort.ExistingAutomation = new StudioMemberAutomationView(
            ScopeId,
            TeamId,
            MemberId,
            $"provision-{PublishedServiceId}",
            PublishedServiceId,
            "Existing monitor",
            "old prompt",
            "0 9 * * *",
            "UTC",
            true,
            "active",
            DateTimeOffset.UtcNow.AddHours(1),
            string.Empty,
            "studio-workflow-provision-create:old",
            1,
            false,
            DateTimeOffset.UtcNow.AddMinutes(1),
            null,
            10)
        {
            TargetRevisionId = "rev-old",
        };

        var response = await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", "new prompt")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

        schedulePort.LastCreateRequest.Should().BeNull();
        schedulePort.LastReplaceRequest.Should().NotBeNull();
        schedulePort.LastReplaceRequest!.ScheduleId.Should().Be($"provision-{PublishedServiceId}");
        schedulePort.LastReplaceRequest.OperationId.Should()
            .StartWith("studio-workflow-provision-replace:");
        response.ScheduleId.Should().Be($"provision-{PublishedServiceId}");
    }

    [Fact]
    public async Task ProvisionAsync_PropagatesScheduleEnsureFailure()
    {
        var member = NewMemberService();
        var schedule = new RecordingScheduleService
        {
            ThrowOnEnsure = new InvalidOperationException("cron is invalid"),
        };
        var sut = NewService(member, schedule);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            Caller,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor")
            {
                TeamId = TeamId,
                AuthenticatedOwner = TestAuthenticatedOwner(),
            });

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
        IWorkflowExternalCapabilityAdmissionService admission) =>
        NewService(member, schedule, admission, out _);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        out FakeTimeProvider time) =>
        NewService(member, schedule, new StudioWorkflowCapabilityAdmissionTestService(), out time);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        out RecordingBindingPort bindingPort,
        out RecordingWorkflowSchedulePort schedulePort)
    {
        bindingPort = new RecordingBindingPort(member, WorkflowId, RevisionId);
        schedulePort = new RecordingWorkflowSchedulePort(schedule);
        return new StudioWorkflowProvisioningService(
            member,
            bindingPort,
            ImmediateScheduleCommand(schedulePort),
            new StudioWorkflowCapabilityAdmissionTestService());
    }

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingScheduleService schedule,
        IWorkflowExternalCapabilityAdmissionService admission,
        out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new StudioWorkflowProvisioningService(
            member,
            new RecordingBindingPort(member, WorkflowId, RevisionId),
            ImmediateScheduleCommand(schedule, time),
            admission);
    }

    private static IStudioWorkflowScheduleProvisioningCommandPort ImmediateScheduleCommand(
        RecordingScheduleService schedule,
        FakeTimeProvider? time = null) =>
        ImmediateScheduleCommand(new RecordingWorkflowSchedulePort(schedule), time);

    private static IStudioWorkflowScheduleProvisioningCommandPort ImmediateScheduleCommand(
        RecordingWorkflowSchedulePort schedulePort,
        FakeTimeProvider? time = null) =>
        new ImmediateScheduleProvisioningCommandPort(
            new StudioWorkflowScheduleProvisioningExecutor(schedulePort),
            time ?? new FakeTimeProvider());

    private static WorkflowCapabilityAdmissionPlan CapabilityPlan(
        params ExternalWorkflowCapabilityRef[] capabilities)
    {
        var plan = new WorkflowCapabilityAdmissionPlan();
        for (var index = 0; index < capabilities.Length; index++)
        {
            plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = $"monitor/call-{index}",
                Capability = capabilities[index],
            });
        }
        return plan;
    }

    private static ExternalWorkflowCapabilityRef DurableExplicitRequestCapability(string userServiceId)
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.ReadOnly,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        return new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = new NyxIdRequestSelector
                {
                    UserServiceId = userServiceId,
                    Method = NyxIdRequestMethod.Get,
                    PathTemplate = "/api/resources/{resource_id}",
                    BodyMode = NyxIdRequestBodyMode.None,
                    ResponseMode = NyxIdRequestResponseMode.Text,
                },
                ServiceSlugSnapshot = "service-alpha",
                ContractDigest = "contract-alpha",
                ExecutionPolicy = policy,
            },
        };
    }

    private static WorkflowExternalCapabilityAdmissionException DurableAuthorizationUnavailable(
        ExternalWorkflowCapabilityRef capability,
        bool includeAdditionalBlocker = false)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            SelectedCapability = capability.Clone(),
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                    Code = "DURABLE_AUTHORIZATION_UNAVAILABLE",
                    SafeMessage = "The current authorization catalog does not prove this durable grant.",
                },
            },
        };
        if (includeAdditionalBlocker)
        {
            readiness.Blockers.Add(new ExternalCapabilityBlocker
            {
                Status = ExternalCapabilityReadinessStatus.ContractDrift,
                Code = "CONTRACT_DRIFT",
                SafeMessage = "The capability contract changed.",
            });
        }

        return new WorkflowExternalCapabilityAdmissionException(readiness);
    }

    private static RecordingMemberService NewMemberService() =>
        new()
        {
            BindingRunId = BindingRunId,
            CreateResultFactory = static (scopeId, request) => new StudioMemberSummaryResponse(
                MemberId: "m-alpha",
                ScopeId: scopeId,
                DisplayName: request.DisplayName,
                Description: string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: "svc-alpha",
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                TeamId = request.TeamId,
            },
        };

    private static AuthenticatedAuthorizationOwnerContext TestAuthenticatedOwner(
        string platform = "nyxid",
        string tenant = "",
        string ownerSubject = "owner-alpha") =>
        new(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = ownerSubject,
            },
            platform,
            tenant,
            ownerSubject,
            "binding-alpha");

    private sealed class ImmediateScheduleProvisioningCommandPort(
        IStudioWorkflowScheduleProvisioningExecutor executor,
        TimeProvider timeProvider) : IStudioWorkflowScheduleProvisioningCommandPort
    {
        public StudioWorkflowScheduleProvisioningIntent? LastIntent { get; private set; }

        public async Task<StudioWorkflowScheduleProvisioningAcceptance> AcceptAsync(
            StudioWorkflowScheduleProvisioningIntent intent,
            CancellationToken ct = default)
        {
            LastIntent = intent;
            var oneShotFireAt = intent.ScheduleMode == ScheduledDispatchScheduleMode.OneShotAtUtc
                ? timeProvider.GetUtcNow().AddSeconds(intent.OneShotDelaySeconds)
                : (DateTimeOffset?)null;
            var result = await executor.ExecuteAsync(
                new StudioWorkflowScheduleProvisioningExecution(intent, oneShotFireAt),
                ct);
            if (!result.Success)
                throw new InvalidOperationException(result.Detail);

            return new StudioWorkflowScheduleProvisioningAcceptance(
                true,
                intent.ProvisioningId,
                StudioWorkflowScheduleProvisioningStatusNames.Succeeded,
                result.ScheduleId);
        }
    }

    private sealed class RecordingScheduleProvisioningCommandPort
        : IStudioWorkflowScheduleProvisioningCommandPort
    {
        public StudioWorkflowScheduleProvisioningIntent? Intent { get; private set; }

        public List<StudioWorkflowScheduleProvisioningIntent> Intents { get; } = [];

        public Task<StudioWorkflowScheduleProvisioningAcceptance> AcceptAsync(
            StudioWorkflowScheduleProvisioningIntent intent,
            CancellationToken ct = default)
        {
            Intent = intent;
            Intents.Add(intent);
            return Task.FromResult(new StudioWorkflowScheduleProvisioningAcceptance(
                true,
                intent.ProvisioningId,
                StudioWorkflowScheduleProvisioningStatusNames.PendingBinding));
        }
    }

    private sealed class RecordingBindingPort : IStudioMemberWorkflowBindingPort
    {
        private readonly RecordingMemberService _member;
        private readonly string? _acceptedWorkflowId;
        private readonly string? _acceptedRevisionId;

        public RecordingBindingPort(
            RecordingMemberService member,
            string? acceptedWorkflowId,
            string? acceptedRevisionId)
        {
            _member = member;
            _acceptedWorkflowId = acceptedWorkflowId;
            _acceptedRevisionId = acceptedRevisionId;
        }

        public StudioMemberWorkflowBindingRequest? LastRequest { get; private set; }

        public StudioMemberWorkflowBindingResult? LastResult { get; private set; }

        public async Task<StudioMemberWorkflowBindingResult> BindAsync(
            StudioMemberWorkflowBindingRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var receipt = await _member.BindAsync(
                request.ScopeId,
                request.MemberId,
                new UpdateStudioMemberBindingRequest(
                    RevisionId: request.RevisionId,
                    Workflow: new StudioMemberWorkflowBindingSpec(
                        request.WorkflowId ?? "workflow-test",
                        [request.WorkflowYaml, .. (request.InlineWorkflowYamls ?? new Dictionary<string, string>())
                            .OrderBy(static item => item.Key, StringComparer.Ordinal)
                            .Select(static item => item.Value)])
                    {
                        CapabilityAdmissionPlan = request.CapabilityAdmission?.ExistingPlan,
                    })
                {
                    CapabilityAdmission = request.CapabilityAdmission,
                },
                ct);
            LastResult = new StudioMemberWorkflowBindingResult(
                true,
                receipt.ScopeId,
                receipt.MemberId,
                StudioMemberWorkflowBindingOperationNames.Bind,
                receipt.Status,
                receipt.BindingRunId,
                receipt.AckStage,
                receipt.BindingRunRole,
                _acceptedWorkflowId,
                _acceptedRevisionId);
            return LastResult;
        }
    }

    private sealed class RecordingCatalogRefreshPort(NyxIdAuthorizationCatalogRefreshResult result) :
        INyxIdAuthorizationCatalogRefreshPort
    {
        public Action? OnRefresh { get; init; }
        public AuthorizationOwnerIdentity? Owner { get; private set; }
        public string? BearerToken { get; private set; }
        public NyxIdAuthorizationCatalogRefreshRequest? Request { get; private set; }

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            NyxIdAuthorizationCatalogRefreshRequest request,
            CancellationToken ct = default)
        {
            Owner = owner.Clone();
            BearerToken = bearerToken;
            Request = request;
            OnRefresh?.Invoke();
            return Task.FromResult(result);
        }

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
            string verifiedOwnerSubject,
            string bearerToken,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingCatalogVisibilityPort(NyxIdAuthorizationCatalogVisibilityResult result) :
        INyxIdAuthorizationCatalogVisibilityPort
    {
        public AuthorizationOwnerIdentity? Owner { get; private set; }
        public long RequiredStateVersion { get; private set; }

        public Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveAsync(
            AuthorizationOwnerIdentity owner,
            long requiredStateVersion,
            CancellationToken ct = default)
        {
            Owner = owner.Clone();
            RequiredStateVersion = requiredStateVersion;
            return Task.FromResult(result);
        }

        public Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveRequiredServicesAsync(
            AuthorizationOwnerIdentity owner,
            long requiredStateVersion,
            IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
            CancellationToken ct = default) =>
            ResolveAsync(owner, requiredStateVersion, ct);
    }

    private sealed class RecordingWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
    {
        private readonly RecordingScheduleService _schedule;

        public RecordingWorkflowSchedulePort(RecordingScheduleService schedule)
        {
            _schedule = schedule;
        }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default) =>
            PreflightForWriteAsync(request, ct);

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            LastPreflightRequest = request;
            _schedule.PreflightRequests.Add(request);
            return Task.FromResult(new StudioMemberWorkflowAuthorizationResult(
                true,
                new ScheduledInvocationAuthorizationPlan
                {
                    PermissionDigest = "permission-digest-alpha",
                    CredentialPolicy = new ScheduledInvocationCredentialPolicy
                    {
                        PolicyVersion = "policy-v1",
                    },
                },
                ScheduledInvocationAuthorizationFailureCode.Unspecified,
                string.Empty));
        }

        public StudioMemberWorkflowScheduleRequest? LastPreflightRequest { get; private set; }

        public StudioMemberWorkflowScheduleRequest? LastCreateRequest { get; private set; }

        public StudioMemberWorkflowScheduleRequest? LastReplaceRequest { get; private set; }

        public StudioMemberWorkflowScheduleResult? LastResult { get; private set; }

        public StudioMemberAutomationView? ExistingAutomation { get; set; }

        public async Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default)
        {
            LastCreateRequest = request;
            _schedule.LastCreateRequest = request;
            var scheduleId = request.ScheduleId ?? "schedule-test";
            var owner = new TeamMemberAutomationOwner(request.ScopeId, request.MemberId, request.TeamId ?? TeamId);
            var receipt = await _schedule.EnsureAsync(
                new ScheduledDispatchConfiguration(
                    ScheduleId: scheduleId,
                    DisplayName: request.DisplayName ?? $"provision-{PublishedServiceId}",
                    Target: new ScheduledDispatchTargetDescriptor(
                        ScheduledDispatchTargetKind.ServiceInvocation,
                        ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                            Identity: new ServiceIdentity
                            {
                                TenantId = request.ScopeId,
                                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                                Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                                ServiceId = PublishedServiceId,
                            },
                            EndpointId: "chat",
                            Payload: Any.Pack(new ChatRequestEvent
                            {
                                Prompt = request.Prompt ?? string.Empty,
                                ScopeId = request.ScopeId,
                            }),
                            Auth: new ScheduledServiceInvocationAuth(
                                new ScheduledServiceInvocationNyxIdCredentialSource(
                                    new ScheduledServiceInvocationNyxIdSubjectRef(
                                        request.AuthenticatedOwner.SubjectPlatform,
                                        request.AuthenticatedOwner.SubjectTenant,
                                        request.AuthenticatedOwner.SubjectExternalUserId),
                                    "proxy")
                                {
                                    Role = ScheduledServiceInvocationNyxIdCredentialRole.Sender,
                                }))),
                    CronExpression: request.ScheduleCron,
                    Timezone: request.ScheduleTimezone,
                    Enabled: request.Enabled,
                    Headers: new Dictionary<string, string>(StringComparer.Ordinal),
                    ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
                    ScheduleMode: request.ScheduleMode,
                    OneShotFireAt: request.OneShotFireAt)
                {
                    TeamAutomationOwner = owner,
                },
                new ScheduledDispatchMutationContext(TeamAutomationOwner: owner),
                ct);
            LastResult = new StudioMemberWorkflowScheduleResult(
                true,
                request.ScopeId,
                request.MemberId,
                receipt.ScheduleId,
                PublishedServiceId,
                "/admin#/observatory",
                "pending");
            return LastResult;
        }

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberWorkflowScheduleResult> ReplaceAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default)
        {
            LastReplaceRequest = request;
            LastResult = new StudioMemberWorkflowScheduleResult(
                true,
                request.ScopeId,
                request.MemberId,
                request.ScheduleId ?? "schedule-test",
                PublishedServiceId,
                "/admin#/observatory",
                "pending");
            return Task.FromResult(LastResult);
        }

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default) =>
            Task.FromResult(
                string.Equals(ExistingAutomation?.ScheduleId, scheduleId, StringComparison.Ordinal)
                    ? ExistingAutomation
                    : null);

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-rolled spy implementing only the members the provisioning service
    /// uses in the async flow: create + bind. <c>GetBindingRunAsync</c> records a
    /// call count and throws if invoked — the service must not poll the bind.
    /// </summary>
    private sealed class RecordingMemberService : Application.Studio.Abstractions.IStudioMemberService
    {
        public string BindingRunId { get; set; } = "bind-run-1";
        public Func<string, CreateStudioMemberRequest, StudioMemberSummaryResponse> CreateResultFactory { get; init; } =
            static (_, _) => throw new InvalidOperationException("Create result factory is required.");

        public bool CreateInvoked { get; private set; }
        public string? CreateScopeId { get; private set; }
        public CreateStudioMemberRequest? CreateRequest { get; private set; }
        public StudioMemberSummaryResponse? LastCreateResult { get; private set; }
        public string? BindScopeId { get; private set; }
        public string? BindMemberId { get; private set; }
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
            if (string.IsNullOrWhiteSpace(request.MemberId))
                throw new InvalidOperationException("Provisioning must submit a member identity candidate.");
            if (new[] { "m-alpha", "wf-alpha", "svc-alpha", "rev-alpha" }.Contains(request.MemberId))
                throw new InvalidOperationException("The member identity candidate must remain distinct from resolved identities.");
            LastCreateResult = CreateResultFactory(scopeId, request);
            return Task.FromResult(LastCreateResult);
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default)
        {
            BindScopeId = scopeId;
            BindMemberId = memberId;
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
        public StudioMemberWorkflowScheduleRequest? LastCreateRequest { get; set; }
        public List<StudioMemberWorkflowScheduleRequest> PreflightRequests { get; } = [];
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
            string scheduleId, string reason, ScheduledDispatchMutationContext? context = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> DisableAsync(
            string scheduleId, string reason, ScheduledDispatchMutationContext? context = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> DeleteAsync(
            string scheduleId, string reason, ScheduledDispatchMutationContext? context = null, CancellationToken ct = default) =>
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
            string scheduleId, ScheduledDispatchMutationContext? context = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Manual-set time provider so the typed one-shot fire time is deterministic.
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
