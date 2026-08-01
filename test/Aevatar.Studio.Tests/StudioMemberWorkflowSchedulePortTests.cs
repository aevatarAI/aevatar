using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowSchedulePortTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.UnixEpoch.AddDays(20_000);

    [Fact]
    public async Task EnsureAsync_HappyPath_SchedulesExistingBoundWorkflowMember()
    {
        var memberService = new RecordingMemberService
        {
            Detail = CreateWorkflowMemberDetail(),
        };
        var scheduleService = new RecordingScheduleService();
        var planner = new RecordingAuthorizationPlanner();
        var sut = NewPort(scheduleService, memberService, planner);

        var result = await ScheduleAsync(sut, Request("scope-1", "member-1") with
        {
            Prompt = "run digest",
            DisplayName = "Daily digest",
        });

        result.Success.Should().BeTrue();
        result.Status.Should().Be("pending");
        result.ScopeId.Should().Be("scope-1");
        result.MemberId.Should().Be("member-1");
        result.ScheduleId.Should().Be(scheduleService.Configuration!.ScheduleId);
        result.PublishedServiceId.Should().Be("published-member-1");
        result.ObservatoryUrl.Should().Be("/workflow/observatory");

        memberService.GetScopeId.Should().Be("scope-1");
        memberService.GetMemberId.Should().Be("member-1");
        memberService.CreateCallCount.Should().Be(0);
        memberService.BindCallCount.Should().Be(0);
        planner.Requests.Should().HaveCount(2, "preflight and write-side revalidation each read once");

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
        chat.LlmControl.ModelOverride.Should().Be("gpt-5.5");
        chat.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        invocation.AuthorizationFact.Should().NotBeNull();
        var fact = invocation.AuthorizationFact!;
        fact.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        fact.OwnerLLMSelection.Should().BeEquivalentTo(planner.Result.Plan!.OwnerLlmSelection);
        fact.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        fact.ServiceGrants.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledInvocationAuthorizationServiceGrant("nyx-service-alpha", ["nyx-node-alpha"], false));
        fact.Authority.Should().BeEquivalentTo(new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationAuthority(
            3, 5, 7, 11, 13,
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            "catalog-digest-alpha",
            "scope-plan-contract/v1",
            "scope-plan-policy/v1",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z")));
        configuration.CredentialRequirementTargetKind.Should()
            .Be(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService);
        var decision = scheduleService.BeginOperation!.ActivationDecision;
        decision.ScheduleId.Should().Be(configuration.ScheduleId);
        decision.DisplayName.Should().Be(configuration.DisplayName);
        decision.Owner.Should().Be(configuration.TeamAutomationOwner);
        decision.ServiceIdentity.Should().BeEquivalentTo(invocation.Identity);
        decision.ServiceIdentity.Should().NotBeSameAs(invocation.Identity);
        decision.EndpointId.Should().Be(invocation.EndpointId);
        decision.Payload.Should().Be(invocation.Payload);
        decision.Payload.Should().NotBeSameAs(invocation.Payload);
        decision.CallerAuthority.Should().BeEquivalentTo(invocation.Auth!.CallerAuthority);
        decision.CallerAuthority.Should().NotBeSameAs(invocation.Auth.CallerAuthority);
        decision.AuthorizationFact.Should().BeEquivalentTo(fact);
        decision.AuthorizationFact.Should().NotBeSameAs(fact);
        decision.AuthorizationFact.OwnerLLMSelection.Should().NotBeSameAs(fact.OwnerLLMSelection);
        decision.CronExpression.Should().Be(configuration.CronExpression);
        decision.Timezone.Should().Be(configuration.Timezone);
        decision.Enabled.Should().Be(configuration.Enabled);
        decision.ScheduleKind.Should().Be(configuration.ScheduleKind);
        decision.Headers.Should().Equal(configuration.Headers);
        decision.Headers.Should().NotBeSameAs(configuration.Headers);
        decision.ScheduleMode.Should().Be(configuration.ScheduleMode);
        decision.OneShotFireAt.Should().Be(configuration.OneShotFireAt);
        decision.CredentialRequirementTargetKind.Should()
            .Be(configuration.CredentialRequirementTargetKind);
        scheduleService.BeginOperation.PermissionDigest.Should().Be(decision.AuthorizationFact.PermissionDigest);
        scheduleService.BeginOperation.PolicyVersion.Should().Be(decision.AuthorizationFact.PolicyVersion);
    }

    [Fact]
    public async Task EnsureAsync_UsesMaterializedScheduledCredentialAndStableMemberOwner()
    {
        var scheduleService = new RecordingScheduleService();
        var sut = NewPort(scheduleService);

        await ScheduleAsync(sut, Request("scope-1", "member-1") with
        {
            CallerSubjectPlatform = " unverified-platform ",
            CallerSubjectTenant = " unverified-tenant ",
            AuthenticatedOwner = new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                " lark ",
                " tenant-alpha ",
                " sender-alpha ",
                " bnd-owner-alpha "),
        });

        var auth = scheduleService.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey!.ApiKeyId.Should().Be("key-alpha");
        auth.ScheduledInvocationAgentKey.SecretReference.Ref.Should().Be("secret-alpha");
        auth.ScheduledInvocationAgentKey.SecretReference.Purpose.Should()
            .Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        auth.SenderNyxId.Should().BeNull();
        auth.Durable.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
        auth.CallerAuthority.Should().BeEquivalentTo(new ScheduledCallerNyxIdAuthority
        {
            Platform = "lark",
            Tenant = "tenant-alpha",
            ExternalUserId = "sender-alpha",
            Scope = "proxy",
            BindingId = "bnd-owner-alpha",
        });
        scheduleService.Configuration.TeamAutomationOwner.Should()
            .Be(new TeamMemberAutomationOwner("scope-1", "member-1", "team-1"));
    }

    [Fact]
    public async Task GetAsync_ShouldExposeOwnerLLMRuntimeEvidenceFromScheduleReadModel()
    {
        var detail = CreateTeamAutomationDetail(
            RecordingAuthorizationPlanner.Digest,
            RecordingAuthorizationPlanner.PolicyVersion);
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMRouteKind", "nyx_id_user_service");
        SetRequiredStringProperty(
            detail.Schedule,
            "OwnerLLMRoute",
            "/api/v1/proxy/s/chrono-llm-public");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMUserServiceId", "us-chrono");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMServiceSlug", "chrono-llm-public");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMModel", "gpt-5.5");
        var scheduleService = new RecordingScheduleService
        {
            TeamAutomationDetail = detail,
        };
        var port = NewPort(scheduleService);

        var result = await port.GetAsync("scope-1", "team-1", "member-1", "schedule-1");

        result.Should().NotBeNull();
        ReadRequiredStringProperty(result!, "OwnerLLMRouteKind").Should().Be("nyx_id_user_service");
        ReadRequiredStringProperty(result, "OwnerLLMRoute").Should()
            .Be("/api/v1/proxy/s/chrono-llm-public");
        ReadRequiredStringProperty(result, "OwnerLLMUserServiceId").Should().Be("us-chrono");
        ReadRequiredStringProperty(result, "OwnerLLMServiceSlug").Should().Be("chrono-llm-public");
        ReadRequiredStringProperty(result, "OwnerLLMModel").Should().Be("gpt-5.5");
        ReadRequiredStringProperty(result, "NyxIdRevocationStatus").Should().BeEmpty();
        ReadRequiredStringProperty(result, "VaultRevocationStatus").Should().BeEmpty();
        result.StateVersion.Should().Be(detail.Schedule.StateVersion);
    }

    [Fact]
    public async Task ListAsync_WithMemberId_ShouldResolveMemberAndUseExactTeamAutomationOwner()
    {
        var scheduleService = new RecordingScheduleService
        {
            TeamAutomationList = new ScheduledDispatchListResult(
                [CreateTeamAutomationDetail(
                    RecordingAuthorizationPlanner.Digest,
                    RecordingAuthorizationPlanner.PolicyVersion).Schedule],
                "next-member",
                1),
        };
        var memberService = new RecordingMemberService
        {
            Detail = CreateWorkflowMemberDetail(teamId: "team-1"),
        };
        var port = NewPort(scheduleService, memberService);

        var result = await port.ListAsync(
            "scope-1",
            "team-1",
            "member-1",
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        memberService.GetMemberId.Should().Be("member-1");
        scheduleService.LastTeamAutomationListOwner.Should()
            .Be(new TeamMemberAutomationOwner("scope-1", "member-1", "team-1"));
        scheduleService.LastTeamAutomationListTake.Should().Be(25);
        scheduleService.LastTeamAutomationListCursor.Should().Be("cursor-1");
        scheduleService.LastTeamAutomationListIncludeTotalCount.Should().BeTrue();
        scheduleService.LastListQuery.Should().BeNull();
        result.Items.Should().ContainSingle().Which.MemberId.Should().Be("member-1");
    }

    [Fact]
    public async Task ListAsync_WithoutMemberId_ShouldUseTeamWideScheduleReadModelQuery()
    {
        var summary = CreateTeamAutomationDetail(
            RecordingAuthorizationPlanner.Digest,
            RecordingAuthorizationPlanner.PolicyVersion).Schedule with
        {
            TeamOwnerScopeId = "scope-1",
            TeamId = "team-1",
            TeamOwnerMemberId = "member-2",
            ServiceId = "published-member-2",
        };
        var scheduleService = new RecordingScheduleService
        {
            ListResult = new ScheduledDispatchListResult([summary], "next-team", 1),
        };
        var memberService = new RecordingMemberService
        {
            Detail = CreateWorkflowMemberDetail(teamId: "team-1"),
        };
        var port = NewPort(scheduleService, memberService);

        var result = await port.ListAsync(
            " scope-1 ",
            " team-1 ",
            memberId: null,
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        memberService.GetMemberId.Should().BeNull();
        scheduleService.LastListQuery.Should().Be(new ScheduledDispatchListQuery(
            Take: 25,
            Cursor: "cursor-1",
            IncludeTotalCount: true,
            TeamAutomationScopeId: "scope-1",
            TeamAutomationTeamId: "team-1",
            TeamAutomationMemberId: null,
            ExcludeCompletedTeamAutomationDeletions: true));
        scheduleService.LastTeamAutomationListOwner.Should().BeNull();
        var item = result.Items.Should().ContainSingle().Subject;
        item.ScopeId.Should().Be("scope-1");
        item.TeamId.Should().Be("team-1");
        item.MemberId.Should().Be("member-2");
        item.PublishedServiceId.Should().Be("published-member-2");
        result.NextCursor.Should().Be("next-team");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_ShouldPreserveDistinctRevocationTrackStatusesFromScheduleReadModel()
    {
        var detail = CreateTeamAutomationDetail(
            RecordingAuthorizationPlanner.Digest,
            RecordingAuthorizationPlanner.PolicyVersion);
        SetRequiredStringProperty(detail.Schedule, "NyxIdRevocationStatus", "nyx-track-terminal");
        SetRequiredStringProperty(detail.Schedule, "VaultRevocationStatus", "vault-track-terminal");
        var scheduleService = new RecordingScheduleService
        {
            TeamAutomationDetail = detail,
        };
        var port = NewPort(scheduleService);

        var result = await port.GetAsync("scope-1", "team-1", "member-1", "schedule-1");

        result.Should().NotBeNull();
        ReadRequiredStringProperty(result!, "NyxIdRevocationStatus").Should().Be("nyx-track-terminal");
        ReadRequiredStringProperty(result, "VaultRevocationStatus").Should().Be("vault-track-terminal");
        result.RevocationPending.Should().Be(detail.Schedule.RevocationPending);
    }

    [Fact]
    public async Task EnsureAsync_WhenVerifiedOwnerBindingMissing_ShouldFailClosed()
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer();
        var sut = NewPort(scheduleService, materializer: materializer);
        var request = Request("scope-1", "member-1") with
        {
            AuthenticatedOwner = new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                "lark",
                "tenant-alpha",
                "sender-alpha",
                " "),
        };

        var act = () => ScheduleAsync(sut, request);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("authenticated_authorization_owner_binding_missing");
        materializer.MaterializeCallCount.Should().Be(0);
        materializer.RevokeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(0);
        scheduleService.Configurations.Should().BeEmpty();
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
        planner.Requests[0].InvocationTarget.StudioMember.MemberId.Should().Be("member-1");
        planner.Requests[0].InvocationTarget.StudioMember.PublishedServiceId.Should().Be("published-member-1");
    }

    [Fact]
    public async Task PreflightAsync_ShouldDeriveStableCredentialExpiryFromServerPolicy()
    {
        var planner = new RecordingAuthorizationPlanner();
        var port = NewPort(new RecordingScheduleService(), planner: planner);

        await port.PreflightAsync(Request("scope-1", "member-1"));

        planner.Requests.Should().ContainSingle();
        planner.Requests[0].EvaluatedAtUtc.Should().Be(TestNow);
        planner.Requests[0].ExpiresAtUtc.Should().Be(
            new StudioMemberWorkflowSchedulePolicy().ResolveCredentialExpiresAtUtc(TestNow));
    }

    [Fact]
    public void UpdateCommand_ShouldCarryOnlyTransientProvisioningBearerInput()
    {
        var bearerProperty = typeof(StudioMemberAutomationUpdateCommand)
            .GetProperty("ProvisioningBearerToken");

        bearerProperty.Should().NotBeNull();
        bearerProperty!.CanRead.Should().BeTrue();
        bearerProperty.SetMethod.Should().NotBeNull();
    }

    [Fact]
    public void CatalogRefreshUnavailable_ShouldUseTypedSanitizedApplicationFailure()
    {
        var exceptionType = typeof(StudioMemberAutomationPlanConflictException).Assembly.GetType(
            "Aevatar.Studio.Application.Provisioning." +
            "StudioMemberAutomationCatalogRefreshUnavailableException");

        exceptionType.Should().NotBeNull();
        exceptionType.Should().BeAssignableTo<Exception>();
        var exception = Activator.CreateInstance(exceptionType!).Should().BeAssignableTo<Exception>().Subject;
        exception.Message.Should().Be("The authorization catalog could not be refreshed. Retry this request.");
    }

    [Theory]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_not_found")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_invalidated")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
        "nyxid_catalog_snapshot_stale")]
    public async Task PreflightAsync_WhenCatalogSnapshotUnavailableAndBearerAvailable_ShouldReturnPlannerResultWithoutSideEffects(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail)
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            failureCode,
            detail));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var request = Request("scope-1", "member-1");
        var port = NewPort(
            new RecordingScheduleService(),
            planner: planner,
            catalogRefresh: refresh);

        var result = await port.PreflightAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(failureCode);
        result.Detail.Should().Be(detail);
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightAsync_WhenCatalogSnapshotMissingAndTypedAuthorityAvailable_ShouldNotIssueFreshTokenOrRefresh()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_not_found"));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var tokenProvider = new RecordingWorkflowCallerAccessTokenProvider();
        var request = Request("scope-1", "member-1") with
        {
            ProvisioningBearerToken = null,
        };
        var port = NewPort(
            new RecordingScheduleService(),
            planner: planner,
            catalogRefresh: refresh,
            callerAccessTokenProvider: tokenProvider);

        var result = await port.PreflightAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound);
        result.Detail.Should().Be("nyxid_catalog_snapshot_not_found");
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(0);
        tokenProvider.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_not_found")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_invalidated")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
        "nyxid_catalog_snapshot_stale")]
    public async Task PreflightForWriteAsync_WhenCatalogSnapshotUnavailableAndBearerAvailable_ShouldRefreshAndRetryPlanner(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail)
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            failureCode,
            detail,
            requiredNyxIdServices: [new NyxIdUserServiceCapabilityRef { UserServiceId = "nyx-service-alpha" }]));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var materializer = new RecordingCredentialMaterializer();
        var scheduleService = new RecordingScheduleService();
        var request = Request("scope-1", "member-1");
        var port = NewPort(
            scheduleService,
            planner: planner,
            materializer: materializer,
            catalogRefresh: refresh);

        var result = await port.PreflightForWriteAsync(request);

        result.Success.Should().BeTrue();
        result.Plan!.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        planner.Requests.Should().HaveCount(2);
        refresh.RefreshCallCount.Should().Be(1);
        refresh.LastOwner.Should().BeEquivalentTo(request.AuthenticatedOwner.Owner);
        refresh.LastBearerToken.Should().Be("bearer-alpha");
        refresh.LastRequiredServices.Select(static service => service.UserServiceId)
            .Should().Equal("nyx-service-alpha");
        materializer.MaterializeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenCatalogRefreshIsUnstable_ShouldThrowTypedRefreshUnavailable()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated",
            requiredNyxIdServices: [new NyxIdUserServiceCapabilityRef { UserServiceId = "nyx-service-alpha" }]));
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                "api_key_scope_plan_route_unresolved"),
        };
        var materializer = new RecordingCredentialMaterializer();
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(
            scheduleService,
            planner: planner,
            materializer: materializer,
            catalogRefresh: refresh);

        var act = () => port.PreflightForWriteAsync(Request("scope-1", "member-1"));

        await act.Should().ThrowAsync<StudioMemberAutomationCatalogRefreshUnavailableException>()
            .WithMessage("The authorization catalog could not be refreshed. Retry this request.");
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(1);
        materializer.MaterializeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenCatalogSnapshotUnavailableWithoutRequiredServices_ShouldNotRefresh()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_not_found",
            requiredNyxIdServices: []));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var tokenProvider = new RecordingWorkflowCallerAccessTokenProvider();
        var request = Request("scope-1", "member-1") with
        {
            ProvisioningBearerToken = null,
        };
        var port = NewPort(
            new RecordingScheduleService(),
            planner: planner,
            catalogRefresh: refresh,
            callerAccessTokenProvider: tokenProvider);

        var result = await port.PreflightForWriteAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound);
        result.Detail.Should().Be("nyxid_catalog_refresh_required_services_unavailable:nyxid_catalog_snapshot_not_found");
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(0);
        tokenProvider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenRetryReturnsNonCatalogFailureWithoutCatalogObservation_ShouldReturnPlannerFailure()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated",
            observedCatalogStateVersion: 22));
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
            "owner_llm_exact_service_identity_unavailable"));
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var materializer = new RecordingCredentialMaterializer();
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(
            scheduleService,
            planner: planner,
            materializer: materializer,
            catalogRefresh: refresh);

        var result = await port.PreflightForWriteAsync(Request("scope-1", "member-1"));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable);
        result.Detail.Should().Be("owner_llm_exact_service_identity_unavailable");
        planner.Requests.Should().HaveCount(2);
        refresh.RefreshCallCount.Should().Be(1);
        materializer.MaterializeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenRetryReturnsNonCatalogFailureFromStaleCatalogObservation_ShouldThrowProjectionPending()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated",
            observedCatalogStateVersion: 22));
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
            "owner_llm_exact_service_identity_unavailable",
            observedCatalogStateVersion: 22));
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var materializer = new RecordingCredentialMaterializer();
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(
            scheduleService,
            planner: planner,
            materializer: materializer,
            catalogRefresh: refresh);

        var act = () => port.PreflightForWriteAsync(Request("scope-1", "member-1"));

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        planner.Requests.Should().HaveCount(2);
        refresh.RefreshCallCount.Should().Be(1);
        materializer.MaterializeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenBearerMissingButTypedAuthorityAvailable_ShouldIssueFreshTokenAndRefresh()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated"));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var tokenProvider = new RecordingWorkflowCallerAccessTokenProvider();
        var port = NewPort(
            new RecordingScheduleService(),
            planner: planner,
            catalogRefresh: refresh,
            callerAccessTokenProvider: tokenProvider);

        var result = await port.PreflightForWriteAsync(Request("scope-1", "member-1") with
        {
            ProvisioningBearerToken = null,
        });

        result.Success.Should().BeTrue();
        refresh.LastBearerToken.Should().Be("issued-bearer-alpha");
        tokenProvider.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenCommittedRefreshVersionIsNotYetVisible_ShouldThrowProjectionPending()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated",
            observedCatalogStateVersion: 22));
        var stalePlan = RecordingAuthorizationPlanner.SuccessResult().Plan!.Clone();
        stalePlan.CatalogAuthority.ActorStateVersion = 22;
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Succeeded(stalePlan));
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(
            scheduleService,
            planner: planner,
            catalogRefresh: refresh);

        var act = () => port.PreflightForWriteAsync(Request("scope-1", "member-1"));

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        planner.Requests.Should().HaveCount(2);
        refresh.RefreshCallCount.Should().Be(1);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenSupersededRefreshVersionIsAheadOfObservedSnapshot_ShouldThrowProjectionPending()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_invalidated",
            observedCatalogStateVersion: 22));
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Superseded,
                "nyxid_catalog_refresh_superseded",
                StateVersion: 23),
        };
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(
            scheduleService,
            planner: planner,
            catalogRefresh: refresh);

        var act = () => port.PreflightForWriteAsync(Request("scope-1", "member-1"));

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(1);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_not_found")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_invalidated")]
    [InlineData(
        ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
        "nyxid_catalog_snapshot_stale")]
    public async Task PreflightAsync_WhenCatalogSnapshotUnavailableWithoutBearer_ShouldReturnSinglePlannerResultWithoutSideEffects(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail)
    {
        var planner = new RecordingAuthorizationPlanner
        {
            Result = ScheduledInvocationAuthorizationPlanResult.Failed(failureCode, detail),
        };
        var downstreamCalls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = downstreamCalls };
        var refresh = new RecordingCatalogRefreshPort { Calls = downstreamCalls };
        var tokenProvider = new RecordingWorkflowCallerAccessTokenProvider();
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(
            scheduleService,
            planner: planner,
            materializer: materializer,
            catalogRefresh: refresh,
            callerAccessTokenProvider: tokenProvider);

        var result = await port.PreflightAsync(Request("scope-1", "member-1") with
        {
            AuthenticatedOwner = new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                "lark",
                "tenant-alpha",
                "sender-alpha",
                string.Empty),
            ProvisioningBearerToken = null,
        });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(failureCode);
        result.Detail.Should().Be(detail);
        planner.Requests.Should().ContainSingle();
        refresh.RefreshCallCount.Should().Be(0);
        tokenProvider.Requests.Should().BeEmpty();
        materializer.MaterializeCallCount.Should().Be(0);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        downstreamCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenBearerMissingButTypedAuthorityAvailable_ShouldIssueFreshTokenAndPersistAgentKeyOnly()
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer();
        var tokenProvider = new RecordingWorkflowCallerAccessTokenProvider();
        var port = NewPort(
            scheduleService,
            materializer: materializer,
            callerAccessTokenProvider: tokenProvider);

        var result = await ScheduleAsync(port, Request("scope-1", "member-1") with
        {
            ProvisioningBearerToken = null,
        });

        result.Success.Should().BeTrue();
        tokenProvider.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
        materializer.BearerToken.Should().Be("issued-bearer-alpha");
        var auth = scheduleService.Configuration!.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey!.ApiKeyId.Should().Be("key-alpha");
        auth.SenderNyxId.Should().BeNull();
        auth.Durable.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
    }

    [Fact]
    public async Task PreflightAsync_WhenMemberBelongsToAnotherTeam_ShouldReturnGenericNotFound()
    {
        var port = NewPort(new RecordingScheduleService());

        var action = () => port.PreflightAsync(Request("scope-1", "member-1") with { TeamId = "team-other" });

        await action.Should().ThrowAsync<StudioMemberAutomationNotFoundException>()
            .WithMessage("The requested Team automation was not found.");
    }

    [Fact]
    public async Task PreflightAsync_WhenMemberDoesNotExist_ShouldReturnGenericNotFound()
    {
        var port = NewPort(
            new RecordingScheduleService(),
            new RecordingMemberService { Detail = null });

        var action = () => port.PreflightAsync(Request("scope-1", "missing-member"));

        await action.Should().ThrowAsync<StudioMemberAutomationNotFoundException>()
            .WithMessage("The requested Team automation was not found.");
    }

    [Fact]
    public async Task PreflightForWriteAsync_WhenMemberReadModelMissingButAcceptedBindingProvided_ShouldUseAcceptedBindingContext()
    {
        var memberService = new RecordingMemberService { Detail = null };
        var planner = new RecordingAuthorizationPlanner();
        var port = NewPort(
            new RecordingScheduleService(),
            memberService,
            planner);
        var request = Request("scope-1", "member-1") with
        {
            AcceptedBinding = new StudioMemberWorkflowAcceptedBindingContext(
                "team-1",
                "published-member-accepted",
                "workflow-accepted",
                "rev-accepted")
            {
                WorkflowEvidence = new ScheduledInvocationWorkflowEvidence(
                    StateVersion: 0,
                    ExternalCapabilities: [],
                    OwnerLLMRouteRequired: false,
                    ServiceGrantRequirement: AuthorizationGrantRequirement.NotRequired),
            },
        };

        var result = await port.PreflightForWriteAsync(request);

        result.Success.Should().BeTrue();
        memberService.GetScopeId.Should().Be("scope-1");
        memberService.GetMemberId.Should().Be("member-1");
        planner.Requests.Should().ContainSingle();
        var target = planner.Requests[0].InvocationTarget.StudioMember;
        target.TeamId.Should().Be("team-1");
        target.MemberId.Should().Be("member-1");
        target.PublishedServiceId.Should().Be("published-member-accepted");
        target.DraftWorkflowId.Should().Be("workflow-accepted");
        target.WorkflowRevisionId.Should().Be("rev-accepted");
        planner.Requests[0].TrustedMemberEvidence.Should().BeEquivalentTo(
            new ScheduledInvocationMemberEvidence(
                StateVersion: 0,
                DraftWorkflowId: "workflow-accepted",
                WorkflowRevisionId: "rev-accepted",
                PublishedServiceId: "published-member-accepted"));
        planner.Requests[0].TrustedWorkflowEvidence.Should().BeEquivalentTo(
            new ScheduledInvocationWorkflowEvidence(
                StateVersion: 0,
                ExternalCapabilities: [],
                OwnerLLMRouteRequired: false,
                ServiceGrantRequirement: AuthorizationGrantRequirement.NotRequired));
    }

    [Fact]
    public void ToScheduleAuthorizationFact_ShouldMapMixedDirectAndNodeBackedServicesPerService()
    {
        var plan = new RecordingAuthorizationPlanner().Result.Plan!.Clone();
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "nyx-service-direct",
            NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        });

        var fact = StudioMemberWorkflowSchedulePort.ToScheduleAuthorizationFact(plan);

        fact.ServiceGrants.Should().HaveCount(2);
        fact.ServiceGrants[0].ServiceId.Should().Be("nyx-service-alpha");
        fact.ServiceGrants[0].NodeIds.Should().Equal("nyx-node-alpha");
        fact.ServiceGrants[0].NodeGrantsNotRequired.Should().BeFalse();
        fact.ServiceGrants[1].ServiceId.Should().Be("nyx-service-direct");
        fact.ServiceGrants[1].NodeIds.Should().BeEmpty();
        fact.ServiceGrants[1].NodeGrantsNotRequired.Should().BeTrue();
        fact.OwnerLLMSelection.Should().BeEquivalentTo(plan.OwnerLlmSelection);
        fact.OwnerLLMSelection.Should().NotBeSameAs(plan.OwnerLlmSelection);
        fact.GetType().GetProperties().Should()
            .NotContain(property => property.Name == "NodeGrants");
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

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage("snapshot_missing");
        conflict.Which.Code.Should().Be("authorization_plan_changed");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WhenCatalogSnapshotInvalidatedAndBearerAvailable_ShouldRefreshAndRetryRevalidation()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort { Calls = calls };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls);
        var retryNow = TestNow.AddSeconds(7);
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh,
            timeProvider: new SequenceTimeProvider(TestNow, retryNow));
        var request = Request("scope-1", "member-1");

        var result = await port.CreateAsync(request, RecordingAuthorizationPlanner.Digest);

        result.Success.Should().BeTrue();
        result.Status.Should().Be("pending");
        refresh.RefreshCallCount.Should().Be(1);
        refresh.LastOwner.Should().BeEquivalentTo(request.AuthenticatedOwner.Owner);
        refresh.LastBearerToken.Should().Be("bearer-alpha");
        revalidator.Requests.Should().HaveCount(2);
        revalidator.Requests[1].EvaluatedAtUtc.Should().Be(retryNow);
        revalidator.Requests[1].ExpiresAtUtc.Should().Be(
            new StudioMemberWorkflowSchedulePolicy().ResolveCredentialExpiresAtUtc(retryNow));
        scheduleService.BeginCallCount.Should().Be(1);
        scheduleService.EnsureCallCount.Should().Be(1);
        calls.Should().Equal("revalidate", "refresh", "revalidate", "begin", "complete");
    }

    [Fact]
    public async Task CreateAsync_WhenCommittedRefreshVersionIsNotYetVisible_ShouldReturnRetryableProjectionPending()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Observed,
                string.Empty,
                StateVersion: 23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            VisibleCatalogStateVersionAfterRefresh = 22,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh", "revalidate");
    }

    [Fact]
    public async Task CreateAsync_WhenSuccessfulSecondReadIsBelowCommittedRefreshVersion_ShouldReturnProjectionPending()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            SuccessfulCatalogStateVersionAfterRefresh = 22,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh", "revalidate");
    }

    [Fact]
    public async Task CreateAsync_WhenSupersededRefreshVersionIsAheadOfObservedSnapshot_ShouldReturnProjectionPending()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Superseded,
                "nyxid_catalog_refresh_superseded",
                StateVersion: 23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            VisibleCatalogStateVersionBeforeRefresh = 22,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh");
    }

    [Fact]
    public async Task CreateAsync_WhenSupersededRefreshIsAlreadyVisible_ShouldReturnRetryableSupersededFailure()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Superseded,
                "nyxid_catalog_refresh_superseded",
                StateVersion: 23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            VisibleCatalogStateVersionBeforeRefresh = 23,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        await act.Should().ThrowAsync<StudioMemberAutomationCatalogRefreshSupersededException>();
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh");
    }

    [Theory]
    [InlineData(NyxIdAuthorizationCatalogRefreshStatus.Failed)]
    [InlineData(NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut)]
    public async Task CreateAsync_WhenCatalogRefreshFailsTransiently_ShouldReturnRetryableUnavailable(
        NyxIdAuthorizationCatalogRefreshStatus refreshStatus)
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                refreshStatus,
                "private-provider-detail-bearer-secret"),
        };
        var port = NewPort(
            scheduleService,
            revalidator: new RefreshAwareAuthorizationRevalidator(refresh, calls),
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        await act.Should().ThrowAsync<StudioMemberAutomationCatalogRefreshUnavailableException>()
            .WithMessage("The authorization catalog could not be refreshed. Retry this request.");
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh");
    }

    [Fact]
    public async Task CreateAsync_WhenCatalogRefreshInfrastructureFails_ShouldReturnRetryableUnavailable()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Exception = new InvalidOperationException("private-provider-detail-bearer-secret"),
        };
        var port = NewPort(
            scheduleService,
            revalidator: new RefreshAwareAuthorizationRevalidator(refresh, calls),
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        await act.Should().ThrowAsync<StudioMemberAutomationCatalogRefreshUnavailableException>()
            .WithMessage("The authorization catalog could not be refreshed. Retry this request.");
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh");
    }

    [Fact]
    public async Task CreateAsync_WhenCatalogRefreshCancelsWithoutCallerCancellation_ShouldReturnRetryableUnavailable()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Exception = new OperationCanceledException("infrastructure-owned-cancellation"),
        };
        var port = NewPort(
            scheduleService,
            revalidator: new RefreshAwareAuthorizationRevalidator(refresh, calls),
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        await act.Should().ThrowAsync<StudioMemberAutomationCatalogRefreshUnavailableException>()
            .WithMessage("The authorization catalog could not be refreshed. Retry this request.");
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh");
    }

    [Theory]
    [InlineData("nyxid_catalog_snapshot_invalidated")]
    [InlineData("nyxid_catalog_snapshot_stale")]
    public async Task CreateAsync_WhenCatalogFailureIsVisibleAtOrBeyondCommittedRefreshVersion_ShouldKeepFailure(
        string failureDetail)
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService { Calls = calls };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            VisibleCatalogStateVersionAfterRefresh = 24,
            FailureDetailAfterRefresh = failureDetail,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);

        var act = () => port.CreateAsync(
            Request("scope-1", "member-1"),
            RecordingAuthorizationPlanner.Digest);

        var conflict = await act.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage(failureDetail);
        conflict.Which.Code.Should().Be("authorization_plan_changed");
        scheduleService.BeginCallCount.Should().Be(0);
        scheduleService.EnsureCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh", "revalidate");
    }

    [Fact]
    public async Task CreateAsync_WhenBeginCommitsNewOperation_ShouldReportNewOperationCommitted()
    {
        var result = await ScheduleAsync(
            NewPort(new RecordingScheduleService()),
            Request("scope-1", "member-1"));

        result.NewOperationCommitted.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WhenBeginReplayDoesNotOwnEffect_ShouldNotMaterializeCredential()
    {
        var scheduleService = new RecordingScheduleService
        {
            BeginOwnsEffectAttempt = false,
            BeginNewOperationCommitted = false,
        };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);

        var result = await ScheduleAsync(port, Request("scope-1", "member-1"));

        result.Status.Should().Be("pending");
        result.NewOperationCommitted.Should().BeFalse();
        scheduleService.BeginCallCount.Should().Be(1);
        scheduleService.EnsureCallCount.Should().Be(0);
        materializer.MaterializeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WhenExpiredBeginReplayOwnsEffect_ShouldNotReportNewOperationCommitted()
    {
        var scheduleService = new RecordingScheduleService
        {
            BeginOwnsEffectAttempt = true,
            BeginNewOperationCommitted = false,
        };
        var materializer = new RecordingCredentialMaterializer();

        var result = await ScheduleAsync(
            NewPort(scheduleService, materializer: materializer),
            Request("scope-1", "member-1"));

        result.Success.Should().BeTrue();
        result.NewOperationCommitted.Should().BeFalse();
        scheduleService.BeginCallCount.Should().Be(1);
        materializer.MaterializeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhenCandidateCommitResponseIsAmbiguous_ShouldReuseCommittedCandidateOnRetry()
    {
        var scheduleService = new RecordingScheduleService
        {
            CandidateException = new InvalidOperationException("candidate-observation-ambiguous"),
            CommitCandidateBeforeException = true,
        };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);
        var request = Request("scope-1", "member-1");

        var first = () => ScheduleAsync(port, request);
        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("candidate-observation-ambiguous");
        var retry = await ScheduleAsync(port, request);

        retry.Success.Should().BeTrue();
        materializer.MaterializeCallCount.Should().Be(1);
        materializer.RevokeCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(1);
        scheduleService.EnsureCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhenMaterializerProvesEffectsCleaned_ShouldCommitFencedFailure()
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer
        {
            MaterializeException = new StudioScheduledCredentialMaterializationException(
                "vault-failed",
                effectsCleaned: true,
                new InvalidOperationException("vault-failed")),
        };
        var port = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(port, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<StudioScheduledCredentialMaterializationException>();
        scheduleService.FailCallCount.Should().Be(1);
        scheduleService.CandidateCallCount.Should().Be(0);
        materializer.RevokeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WhenRecoveryEvidenceIsMissing_ShouldCommitStableBlockedFailure()
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer
        {
            MaterializeException = new StudioScheduledCredentialMaterializationException(
                "scheduled_credential_recovery_evidence_missing",
                effectsCleaned: false,
                new InvalidOperationException("scheduled_credential_recovery_evidence_missing"),
                recoveryBlocked: true),
        };
        var port = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(port, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<StudioScheduledCredentialMaterializationException>();
        scheduleService.FailCallCount.Should().Be(1);
        scheduleService.CandidateCallCount.Should().Be(0);
        materializer.RevokeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WhenMaterializerOutcomeIsAmbiguous_ShouldLeaveOperationPending()
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer
        {
            MaterializeException = new InvalidOperationException("materialization-ambiguous"),
        };
        var port = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(port, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("materialization-ambiguous");
        scheduleService.FailCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(0);
        materializer.RevokeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_ShouldUseRichDeleteAndPropagateReasonAndFreshAuthority()
    {
        var scheduleService = new RecordingScheduleService();
        scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
            OwnsEffectAttempt: true,
            NyxIdPending: true,
            VaultPending: true));
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);
        var owner = Request("scope-1", "member-1").AuthenticatedOwner;

        var result = await port.DeleteAsync(
            new StudioMemberAutomationActionCommand(
                "scope-1",
                "team-1",
                "member-1",
                "schedule-1",
                "operation-delete",
                "idempotency-delete")
            {
                Reason = " scheduled_agent_key_canary_cleanup ",
                AuthenticatedOwner = owner,
                ProvisioningBearerToken = "fresh-bearer-sensitive",
            });

        result.Accepted.Should().BeTrue();
        result.Status.Should().Be("pending");
        var call = scheduleService.RichDeleteCalls
            .Should().ContainSingle().Subject;
        call.Owner.Should().Be(new TeamMemberAutomationOwner(
            "scope-1",
            "member-1",
            "team-1"));
        call.Reason.Should().Be("scheduled_agent_key_canary_cleanup");
        materializer.RevocationCalls.Should().ContainSingle().Which.Should().Be(
            ("fresh-bearer-sensitive", true, true));
        scheduleService.CompleteRevocationCallCount.Should().Be(1);

        var serialized = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var forbidden in new[]
                 {
                     "fresh-bearer-sensitive",
                     "nyx-owner-alpha",
                     "binding-alpha",
                     "key-alpha",
                     "secret-alpha",
                     "ApiKeyId",
                     "SecretReference",
                     "VaultReference",
                     "CallerAuthority",
                     "VerifiedBindingId",
                 })
        {
            serialized.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public async Task DeleteAsync_ExactReplay_ShouldContinueOnlyPendingRevocationWithFreshBearer()
    {
        var scheduleService = new RecordingScheduleService();
        scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
            OwnsEffectAttempt: true,
            NyxIdPending: true,
            VaultPending: true));
        scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
            OwnsEffectAttempt: true,
            NyxIdPending: false,
            VaultPending: true));
        var materializer = new RecordingCredentialMaterializer();
        materializer.RevocationResults.Enqueue(
            new StudioScheduledCredentialRevocationResult(
                NyxIdRevoked: true,
                VaultRevoked: false,
                ErrorCode: "credential_revocation_transient"));
        materializer.RevocationResults.Enqueue(
            new StudioScheduledCredentialRevocationResult(
                NyxIdRevoked: true,
                VaultRevoked: true,
                ErrorCode: string.Empty));
        var port = NewPort(scheduleService, materializer: materializer);
        var owner = Request("scope-1", "member-1").AuthenticatedOwner;
        var first = new StudioMemberAutomationActionCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "operation-delete",
            "idempotency-delete")
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            AuthenticatedOwner = owner,
            ProvisioningBearerToken = "fresh-bearer-1",
        };

        await port.DeleteAsync(first);
        await port.DeleteAsync(first with
        {
            ProvisioningBearerToken = "fresh-bearer-2",
        });

        scheduleService.RichDeleteCalls.Should().HaveCount(2);
        scheduleService.RichDeleteCalls.Should().OnlyContain(call =>
            call.ScheduleId == "schedule-1" &&
            call.OperationId == "operation-delete" &&
            call.IdempotencyKey == "idempotency-delete" &&
            call.Reason == "scheduled_agent_key_canary_cleanup");
        materializer.RevocationCalls.Should().Equal(
            ("fresh-bearer-1", true, true),
            ("fresh-bearer-2", false, true));
        scheduleService.RetryRevocationCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RetryRevocationAsync_ShouldUseIdentityOnlyCommandAndExecuteCommittedEffects()
    {
        var scheduleService = new RecordingScheduleService { ReturnPendingRevocationOnRetry = true };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);
        var request = Request("scope-1", "member-1");
        var command = new StudioMemberAutomationRetryRevocationCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1")
        {
            AuthenticatedOwner = request.AuthenticatedOwner,
            ProvisioningBearerToken = "fresh-bearer",
        };

        var result = await port.RetryRevocationAsync(command);

        result.Accepted.Should().BeTrue();
        result.Status.Should().Be("pending");
        scheduleService.RetryRevocationCallCount.Should().Be(1);
        scheduleService.CompleteRevocationCallCount.Should().Be(1);
        materializer.RevokeCallCount.Should().Be(1);
        materializer.MaterializeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RetryRevocationAsync_WhenBothTracksCommit_ShouldEmitAllowlistedCompletionEvidenceOnce()
    {
        var scheduleService = new RecordingScheduleService { ReturnPendingRevocationOnRetry = true };
        var materializer = new RecordingCredentialMaterializer();
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var port = NewPort(
            scheduleService,
            materializer: materializer,
            logger: loggerFactory.CreateLogger<StudioMemberWorkflowSchedulePort>(),
            auditLoggerFactory: loggerFactory);
        var request = Request("scope-1", "member-1");
        var command = new StudioMemberAutomationRetryRevocationCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1")
        {
            AuthenticatedOwner = request.AuthenticatedOwner,
            ProvisioningBearerToken = "fresh-bearer-sensitive",
        };

        await port.RetryRevocationAsync(command);

        var entry = logs.Entries
            .Where(static candidate => candidate.EventId.Name ==
                "StudioMemberAutomationRevocationCompleted")
            .Should().ContainSingle().Subject;
        entry.Category.Should().Be("Aevatar.Studio.MemberAutomation");
        entry.LogLevel.Should().Be(LogLevel.Information);
        entry.EventId.Id.Should().Be(6202);
        entry.Exception.Should().BeNull();

        var properties = entry.State
            .Where(static item => item.Key != "{OriginalFormat}")
            .ToDictionary(static item => item.Key, static item => item.Value);
        properties.Should().HaveCount(9);
        properties.Keys.Should().BeEquivalentTo(
        [
            "ScopeId",
            "TeamId",
            "MemberId",
            "ScheduleId",
            "OperationId",
            "NyxIdRevocationStatus",
            "VaultRevocationStatus",
            "StateVersion",
            "ObservedAtUtc",
        ]);
        properties["ScopeId"].Should().Be("scope-1");
        properties["TeamId"].Should().Be("team-1");
        properties["MemberId"].Should().Be("member-1");
        properties["ScheduleId"].Should().Be("schedule-1");
        properties["OperationId"].Should().Be("operation-delete");
        properties["NyxIdRevocationStatus"].Should().Be("Completed");
        properties["VaultRevocationStatus"].Should().Be("Completed");
        properties["StateVersion"].Should().Be(1L);
        properties["ObservedAtUtc"].Should().Be(TestNow);

        var capturedContent = string.Join(
            '\n',
            entry.State.Select(static item => $"{item.Key}={item.Value}").Append(entry.Message));
        foreach (var forbidden in new[]
                 {
                     "fresh-bearer-sensitive",
                     "key-alpha",
                     "secret-alpha",
                     "idempotency-delete",
                     "nyx-owner-alpha",
                     "PermissionDigest",
                     "ApiKey",
                     "SecretReference",
                     "VaultReference",
                     "CallerAuthority",
                 })
        {
            capturedContent.Should().NotContain(forbidden);
        }
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public async Task RetryRevocationAsync_WhenCompletionIsNotOwnedAndSuccessful_ShouldNotEmitCompletionEvidence(
        bool pending,
        bool ownsEffectAttempt,
        bool nyxIdRevoked,
        bool vaultRevoked)
    {
        var scheduleService = new RecordingScheduleService
        {
            ReturnPendingRevocationOnRetry = pending,
            RetryOwnsEffectAttempt = ownsEffectAttempt,
        };
        var materializer = new RecordingCredentialMaterializer
        {
            NyxIdRevoked = nyxIdRevoked,
            VaultRevoked = vaultRevoked,
        };
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var port = NewPort(
            scheduleService,
            materializer: materializer,
            logger: loggerFactory.CreateLogger<StudioMemberWorkflowSchedulePort>(),
            auditLoggerFactory: loggerFactory);
        var request = Request("scope-1", "member-1");

        await port.RetryRevocationAsync(new StudioMemberAutomationRetryRevocationCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1")
        {
            AuthenticatedOwner = request.AuthenticatedOwner,
            ProvisioningBearerToken = "fresh-bearer-sensitive",
        });

        logs.Entries.Should().NotContain(static candidate =>
            candidate.EventId.Name == "StudioMemberAutomationRevocationCompleted");
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenPermissionDigestChanged_ShouldNotDispatch()
    {
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(scheduleService);

        var action = () => port.ReauthorizeAsync(Request("scope-1", "member-1"), "stale-digest");

        await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage("authorization_plan_changed");
        scheduleService.EnsureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ReauthorizeAsync_WhenAuthorized_ShouldCarryFreshOwnerLLMSelection()
    {
        var planner = new RecordingAuthorizationPlanner();
        var scheduleService = new RecordingScheduleService
        {
            TeamAutomationDetail = CreateTeamAutomationDetail(
                RecordingAuthorizationPlanner.Digest,
                RecordingAuthorizationPlanner.PolicyVersion),
        };
        var port = NewPort(scheduleService, planner: planner);
        var request = Request("scope-1", "member-1") with
        {
            ScheduleId = "schedule-1",
            OperationId = "operation-reauthorize",
            IdempotencyKey = "idempotency-reauthorize",
        };

        var result = await port.ReauthorizeAsync(request, RecordingAuthorizationPlanner.Digest);

        result.Success.Should().BeTrue();
        var configuration = scheduleService.Configuration!;
        configuration.Target.ServiceInvocation!.AuthorizationFact!.OwnerLLMSelection
            .Should().BeEquivalentTo(planner.Result.Plan!.OwnerLlmSelection);
        var chat = configuration.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>();
        chat.LlmControl.ModelOverride.Should().Be("gpt-5.5");
        chat.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
    }

    [Theory]
    [InlineData("stale-digest", RecordingAuthorizationPlanner.PolicyVersion)]
    [InlineData(RecordingAuthorizationPlanner.Digest, "stale-policy")]
    public async Task UpdateAsync_WhenStoredAuthorizationEvidenceDrifts_ShouldRequireReauthorization(
        string permissionDigest,
        string policyVersion)
    {
        var scheduleService = new RecordingScheduleService
        {
            TeamAutomationDetail = CreateTeamAutomationDetail(permissionDigest, policyVersion),
        };
        var port = NewPort(scheduleService);
        var request = Request("scope-1", "member-1");

        var action = () => port.UpdateAsync(new StudioMemberAutomationUpdateCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "0 10 * * *",
            "UTC",
            true,
            "operation-update",
            "idempotency-update",
            request.AuthenticatedOwner));

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>();
        conflict.Which.Code.Should().Be("reauthorization_required");
        scheduleService.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_WhenRefreshedCatalogVersionIsNotVisible_ShouldReturnProjectionPendingWithoutDispatch()
    {
        var calls = new List<string>();
        var scheduleService = new RecordingScheduleService
        {
            Calls = calls,
            TeamAutomationDetail = CreateTeamAutomationDetail(
                RecordingAuthorizationPlanner.Digest,
                RecordingAuthorizationPlanner.PolicyVersion),
        };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            VisibleCatalogStateVersionAfterRefresh = 22,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh);
        var request = Request("scope-1", "member-1");
        var command = new StudioMemberAutomationUpdateCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "0 10 * * *",
            "UTC",
            true,
            "operation-update",
            "idempotency-update",
            request.AuthenticatedOwner)
        {
            ProvisioningBearerToken = "fresh-update-bearer",
        };

        var act = () => port.UpdateAsync(command);

        var pending = await act.Should().ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(23);
        refresh.LastBearerToken.Should().Be("fresh-update-bearer");
        scheduleService.UpdateCallCount.Should().Be(0);
        calls.Should().Equal("revalidate", "refresh", "revalidate");
    }

    [Fact]
    public async Task UpdateAsync_WhenRefreshedCatalogIsVisible_ShouldPreserveCredentialExpiryAndDispatch()
    {
        var calls = new List<string>();
        var existingCredentialExpiry = TestNow.AddHours(20);
        var scheduleService = new RecordingScheduleService
        {
            Calls = calls,
            TeamAutomationDetail = CreateTeamAutomationDetail(
                RecordingAuthorizationPlanner.Digest,
                RecordingAuthorizationPlanner.PolicyVersion),
        };
        var refresh = new RecordingCatalogRefreshPort
        {
            Calls = calls,
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(13),
        };
        var revalidator = new RefreshAwareAuthorizationRevalidator(refresh, calls)
        {
            RequiredExpiresAtUtcAfterRefresh = existingCredentialExpiry,
        };
        var port = NewPort(
            scheduleService,
            revalidator: revalidator,
            catalogRefresh: refresh,
            timeProvider: new FixedTimeProvider(TestNow.AddMinutes(1)));
        var request = Request("scope-1", "member-1");
        var command = new StudioMemberAutomationUpdateCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "0 10 * * *",
            "UTC",
            true,
            "operation-update",
            "idempotency-update",
            request.AuthenticatedOwner)
        {
            ProvisioningBearerToken = "fresh-update-bearer",
        };

        var receipt = await port.UpdateAsync(command);

        receipt.Accepted.Should().BeTrue();
        revalidator.Requests.Should().HaveCount(2);
        revalidator.Requests[1].ExpiresAtUtc.Should().Be(existingCredentialExpiry);
        scheduleService.UpdateCallCount.Should().Be(1);
        var configuration = scheduleService.Configuration!;
        configuration.Target.ServiceInvocation!.AuthorizationFact!.OwnerLLMSelection
            .Should().BeEquivalentTo(RecordingAuthorizationPlanner.SuccessResult().Plan!.OwnerLlmSelection);
        var chat = configuration.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>();
        chat.LlmControl.ModelOverride.Should().Be("gpt-5.5");
        chat.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        calls.Should().Equal("revalidate", "refresh", "revalidate");
    }

    [Fact]
    public async Task CreateAsync_WhenActivationOutcomeIsAmbiguous_ShouldLeaveCommittedCandidateForRecovery()
    {
        var scheduleService = new RecordingScheduleService { EnsureException = new InvalidOperationException("admission-failed") };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(port, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("admission-failed");
        materializer.MaterializeCallCount.Should().Be(1);
        materializer.RevokeCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(1);
        materializer.BearerToken.Should().Be("bearer-alpha");
        materializer.Plan!.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        materializer.OwnerScope!.NyxUserId.Should().Be("nyx-owner-alpha");
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
    public async Task CreateAsync_WhenScheduleIdIsTombstoned_ShouldFailWithoutAllocatingAnotherIdentity()
    {
        var scheduleService = new RecordingScheduleService { TombstonedAttempts = 1 };
        var materializer = new RecordingCredentialMaterializer();

        var action = () => ScheduleAsync(
            NewPort(scheduleService, materializer: materializer),
            Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        scheduleService.EnsureCallCount.Should().Be(1);
        scheduleService.Configurations.Should().ContainSingle();
        scheduleService.Configurations[0].ScheduleId.Should().NotEndWith(".2");
        materializer.RevokeCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(-1, CredentialSecretPurposes.ScheduledInvocationAgentKey,
        "scheduled_credential_expiry_mismatch")]
    [InlineData(25, CredentialSecretPurposes.ScheduledInvocationAgentKey,
        "scheduled_credential_expiry_mismatch")]
    [InlineData(20, "unrelated-purpose",
        "scheduled_credential_purpose_mismatch")]
    public async Task CreateAsync_WhenMaterializedCredentialDoesNotMatchPlan_ShouldRevokeWithoutScheduling(
        int expiresAfterHours,
        string purpose,
        string expectedError)
    {
        var scheduleService = new RecordingScheduleService();
        var materializer = new RecordingCredentialMaterializer
        {
            Credential = CreateCredential(TestNow.AddHours(expiresAfterHours), purpose),
        };
        var sut = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedError);
        scheduleService.EnsureCallCount.Should().Be(0);
        materializer.RevokeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhenActivationNotFoundIsAmbiguous_ShouldKeepCandidateRecoverable()
    {
        var scheduleService = new RecordingScheduleService { TombstonedAttempts = 50 };
        var materializer = new RecordingCredentialMaterializer();
        var sut = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        scheduleService.EnsureCallCount.Should().Be(1);
        scheduleService.Configurations.Should().ContainSingle();
        materializer.RevokeCallCount.Should().Be(0);
        scheduleService.CandidateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureAsync_ShouldUseIdempotencyScopedScheduleIdentity()
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

        var another = new RecordingScheduleService();
        await ScheduleAsync(NewPort(another), Request("scope-1", "member-1") with
        {
            OperationId = "operation-beta",
            IdempotencyKey = "idempotency-beta",
        });
        another.Configuration!.ScheduleId.Should().NotBe(scheduleId);
    }

    [Fact]
    public async Task CreateAsync_ShouldDigestNormalizedSemanticMutationIncludingTeamAssignmentWithoutCredentialMaterial()
    {
        var first = new RecordingScheduleService();
        var replay = new RecordingScheduleService();
        var drifted = new RecordingScheduleService();
        var otherTeam = new RecordingScheduleService();
        var request = Request("scope-1", "member-1") with
        {
            DisplayName = " Daily digest ",
            Prompt = " summarize ",
        };

        await ScheduleAsync(NewPort(first), request);
        await ScheduleAsync(NewPort(replay), request);
        await ScheduleAsync(NewPort(drifted), request with { Prompt = "summarize something else" });
        await ScheduleAsync(
            NewPort(
                otherTeam,
                new RecordingMemberService { Detail = CreateWorkflowMemberDetail(teamId: "team-2") }),
            request with { TeamId = "team-2" });

        first.BeginOperation!.MutationDigest.Should().MatchRegex("^[a-f0-9]{64}$");
        replay.BeginOperation!.MutationDigest.Should().Be(first.BeginOperation.MutationDigest);
        drifted.BeginOperation!.MutationDigest.Should().NotBe(first.BeginOperation.MutationDigest);
        otherTeam.BeginOperation!.MutationDigest.Should().NotBe(first.BeginOperation.MutationDigest);
        first.BeginOperation.CredentialEffectLocator.CredentialOwner.Should().Be(
            new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "nyx-owner-alpha"));
    }

    [Fact]
    public async Task CreateAsync_WhenVerifiedBindingDriftsUnderSameMutationKey_ShouldRejectBeforeSecondCredentialEffect()
    {
        var scheduleService = new RecordingScheduleService { RejectMutationDigestDrift = true };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);
        var request = Request("scope-1", "member-1");
        await ScheduleAsync(port, request);
        var driftedOwner = request.AuthenticatedOwner with
        {
            VerifiedBindingId = "bnd-owner-beta",
        };

        var act = () => ScheduleAsync(port, request with { AuthenticatedOwner = driftedOwner });

        await act.Should().ThrowAsync<ScheduledDispatchConflictException>()
            .WithMessage("team_automation_mutation_conflict");
        scheduleService.BeginCallCount.Should().Be(2);
        materializer.MaterializeCallCount.Should().Be(1);
        scheduleService.CandidateCallCount.Should().Be(1);
        scheduleService.Configurations.Should().ContainSingle();
    }

    private static StudioMemberWorkflowScheduleRequest Request(string scopeId, string memberId) => new(
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleCron: "0 9 * * *",
            ScheduleTimezone: "Asia/Shanghai",
            AuthenticatedOwner: new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                "lark",
                "tenant-alpha",
                "sender-alpha",
                "bnd-owner-alpha"))
        {
            TeamId = "team-1",
            OperationId = "operation-alpha",
            IdempotencyKey = "idempotency-alpha",
            ProvisioningBearerToken = "bearer-alpha",
            CredentialProvisioningKind = "dedicated_scheduled_invocation_agent_key",
            ConfirmedPolicyVersion = RecordingAuthorizationPlanner.PolicyVersion,
        };

    private static void SetRequiredStringProperty(object target, string propertyName, string value)
    {
        var property = target.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} is part of the runtime evidence contract");
        property!.SetValue(target, value);
    }

    private static string ReadRequiredStringProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} is part of the runtime evidence contract");
        return property!.GetValue(value).Should().BeOfType<string>().Which;
    }

    private static StudioMemberWorkflowSchedulePort NewPort(
        RecordingScheduleService schedule,
        RecordingMemberService? memberService = null,
        IScheduledInvocationAuthorizationPlanner? planner = null,
        IScheduledInvocationAuthorizationRevalidator? revalidator = null,
        IStudioScheduledCredentialMaterializer? materializer = null,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefresh = null,
        TimeProvider? timeProvider = null,
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null,
        ILoggerFactory? auditLoggerFactory = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    {
        var resolvedPlanner = planner ?? new RecordingAuthorizationPlanner();
        return new StudioMemberWorkflowSchedulePort(
            memberService ?? new RecordingMemberService { Detail = CreateWorkflowMemberDetail() },
            schedule,
            resolvedPlanner,
            revalidator ?? new RecordingAuthorizationRevalidator(resolvedPlanner),
            materializer ?? new RecordingCredentialMaterializer(),
            timeProvider ?? new FixedTimeProvider(TestNow),
            catalogRefresh,
            logger,
            auditLoggerFactory,
            callerAccessTokenProvider: callerAccessTokenProvider);
    }

    private static async Task<StudioMemberWorkflowScheduleResult> ScheduleAsync(
        StudioMemberWorkflowSchedulePort port,
        StudioMemberWorkflowScheduleRequest request)
    {
        var preflight = await port.PreflightAsync(request);
        preflight.Success.Should().BeTrue();
        return await port.CreateAsync(request, preflight.Plan!.PermissionDigest);
    }

    private static StudioScheduledCredential CreateCredential(DateTimeOffset expiresAtUtc, string purpose) =>
        new(
            "key-alpha",
            new SecretReference
            {
                Ref = "secret-alpha",
                Purpose = purpose,
                OwnerScopeKey = "schedule:test",
                ExpiresAtUnixMs = expiresAtUtc.ToUnixTimeMilliseconds(),
            },
            expiresAtUtc,
            new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "nyx-owner-alpha"));

    private static ScheduledDispatchDetail CreateTeamAutomationDetail(
        string permissionDigest,
        string policyVersion) =>
        new(
            new ScheduledDispatchSummary(
                "schedule-1",
                "Daily digest",
                ScheduledDispatchTargetKind.ServiceInvocation,
                string.Empty,
                Any.Pack(new Empty()).TypeUrl,
                string.Empty,
                "published-member-1",
                "chat",
                "0 9 * * *",
                "UTC",
                true,
                TestNow.AddDays(-1),
                TestNow.AddHours(-1),
                TestNow.AddHours(1),
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                new Dictionary<string, string>(),
                "scheduled-dispatch:schedule-1",
                ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
                TeamOwned: true,
                TeamOwnerScopeId: "scope-1",
                TeamOwnerMemberId: "member-1",
                TeamAutomationLifecycleStatus: TeamAutomationLifecycleStatus.Active,
                CredentialExpiresAt: TestNow.AddHours(20),
                PermissionDigest: permissionDigest,
                PolicyVersion: policyVersion,
                CredentialOwnerAuthority: "nyxid",
                CredentialOwnerKind: "Personal",
                CredentialOwnerSubject: "nyx-owner-alpha"),
            []);

    private static StudioMemberDetailResponse CreateWorkflowMemberDetail(
        string implementationKind = MemberImplementationKindNames.Workflow,
        bool hasBinding = true,
        string? currentBindingRunStatus = null,
        string teamId = "team-1") =>
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
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
            {
                TeamId = teamId,
            },
            ImplementationRef: new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: "workflow-1",
                WorkflowRevision: "rev-1"),
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
        public const string PolicyVersion = "scheduled-invocation-auth/v1";
        public List<ScheduledInvocationAuthorizationRequest> Requests { get; } = [];
        public Queue<ScheduledInvocationAuthorizationPlanResult> Results { get; } = [];
        public ScheduledInvocationAuthorizationPlanResult Result { get; init; } =
            SuccessResult();

        public static ScheduledInvocationAuthorizationPlanResult SuccessResult() =>
            ScheduledInvocationAuthorizationPlanResult.Succeeded(CreatePlan());

        private static ScheduledInvocationAuthorizationPlan CreatePlan()
        {
            var plan = new ScheduledInvocationAuthorizationPlan
            {
                PermissionDigest = Digest,
                Owner = new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "nyx-owner-alpha",
                },
                CredentialPolicy = new ScheduledInvocationCredentialPolicy
                {
                    Scopes = { NyxIdCredentialScope.Read, NyxIdCredentialScope.Proxy },
                    ServiceGrantRequirement = AuthorizationGrantRequirement.Required,
                    NodeGrantRequirement = AuthorizationGrantRequirement.Required,
                    ExpiresAt = Timestamp.FromDateTimeOffset(TestNow.AddHours(24)),
                    PolicyVersion = PolicyVersion,
                },
                CatalogAuthority = new NyxIdCatalogAuthorityStamp
                {
                    ActorStateVersion = 13,
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
                    FreshUntil = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                    ContentDigest = "catalog-digest-alpha",
                    ContractVersion = "scope-plan-contract/v1",
                    PolicyVersion = "scope-plan-policy/v1",
                    EvaluatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
                },
                OwnerLlmSelection = new ScheduledInvocationOwnerLLMSelection
                {
                    RouteKind = LLMRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                    NyxIdUserServiceId = "nyx-llm-service-alpha",
                    ServiceSlugSnapshot = "chrono-llm-public",
                    Model = "gpt-5.5",
                },
            };
            plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
            {
                UserServiceId = "nyx-service-alpha",
                NodeGrantRequirement = AuthorizationGrantRequirement.Required,
                NodeIds = { "nyx-node-alpha" },
            });
            plan.Disclosures.Add(new[]
            {
                ScheduledInvocationDisclosure.DedicatedCredential,
                ScheduledInvocationDisclosure.AevatarSecretCustody,
                ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
                ScheduledInvocationDisclosure.DeleteRevokesCredential,
                ScheduledInvocationDisclosure.PauseResumePreservesCredential,
            });
            plan.SourceStamps.Add(new[]
            {
                Stamp(AuthorizationSourceKind.StudioMember, 3),
                Stamp(AuthorizationSourceKind.WorkflowRevision, 5),
                Stamp(AuthorizationSourceKind.ConnectorCatalog, 7),
                Stamp(AuthorizationSourceKind.OwnerLlmRoute, 11),
            });
            return plan;
        }

        private static AuthorizationSourceStamp Stamp(AuthorizationSourceKind kind, long version) => new()
        {
            SourceKind = kind,
            SourceId = kind.ToString(),
            StateVersion = version,
        };

        public Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
            ScheduledInvocationAuthorizationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Result);
        }
    }

    private sealed class RecordingWorkflowCallerAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Requests { get; } = [];

        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Requests.Add(authority.Clone());
            return Task.FromResult("issued-bearer-alpha");
        }
    }

    private sealed class RecordingCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
    {
        public int RefreshCallCount { get; private set; }
        public AuthorizationOwnerIdentity? LastOwner { get; private set; }
        public string? LastBearerToken { get; private set; }
        public IReadOnlyList<NyxIdUserServiceCapabilityRef> LastRequiredServices { get; private set; } = [];
        public ScheduledInvocationLLMRefreshRequirement? LastLLMTarget { get; private set; }
        public List<string>? Calls { get; init; }
        public Exception? Exception { get; init; }
        public NyxIdAuthorizationCatalogRefreshResult Result { get; init; } =
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(1);

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            CancellationToken ct = default) =>
            RecordRefreshAsync(
                owner,
                bearerToken,
                new NyxIdAuthorizationCatalogRefreshRequest([], LLMTarget: null));

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            NyxIdAuthorizationCatalogRefreshRequest request,
            CancellationToken ct = default) =>
            RecordRefreshAsync(owner, bearerToken, request);

        private Task<NyxIdAuthorizationCatalogRefreshResult> RecordRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            NyxIdAuthorizationCatalogRefreshRequest request)
        {
            RefreshCallCount++;
            Calls?.Add("refresh");
            LastOwner = owner.Clone();
            LastBearerToken = bearerToken;
            LastRequiredServices = request.RequiredServices.Select(static service => service.Clone()).ToArray();
            LastLLMTarget = request.LLMTarget;
            return Exception == null
                ? Task.FromResult(Result)
                : Task.FromException<NyxIdAuthorizationCatalogRefreshResult>(Exception);
        }

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
            string verifiedOwnerSubject,
            string bearerToken,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAuthorizationRevalidator(
        IScheduledInvocationAuthorizationPlanner planner) : IScheduledInvocationAuthorizationRevalidator
    {
        public async Task<ScheduledInvocationAuthorizationValidationResult> RevalidateAsync(
            ScheduledInvocationAuthorizationRequest request,
            ScheduledInvocationAuthorizationConfirmation confirmation,
            CancellationToken ct = default)
        {
            var result = await planner.PlanAsync(request, ct);
            if (!result.Success)
            {
                return ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    result.Detail);
            }
            var plan = result.Plan!;
            return string.Equals(confirmation.PermissionDigest, plan.PermissionDigest, StringComparison.Ordinal) &&
                   string.Equals(confirmation.PolicyVersion, plan.CredentialPolicy.PolicyVersion, StringComparison.Ordinal)
                ? ScheduledInvocationAuthorizationValidationResult.Succeeded(plan)
                : ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    "authorization_plan_changed");
        }
    }

    private sealed class RefreshAwareAuthorizationRevalidator(
        RecordingCatalogRefreshPort refresh,
        List<string> calls) : IScheduledInvocationAuthorizationRevalidator
    {
        public List<ScheduledInvocationAuthorizationRequest> Requests { get; } = [];

        public long VisibleCatalogStateVersionBeforeRefresh { get; init; }
        public long VisibleCatalogStateVersionAfterRefresh { get; init; } = long.MaxValue;
        public long? SuccessfulCatalogStateVersionAfterRefresh { get; init; }
        public string? FailureDetailAfterRefresh { get; init; }
        public DateTimeOffset? RequiredExpiresAtUtcAfterRefresh { get; init; }

        public Task<ScheduledInvocationAuthorizationValidationResult> RevalidateAsync(
            ScheduledInvocationAuthorizationRequest request,
            ScheduledInvocationAuthorizationConfirmation confirmation,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            calls.Add("revalidate");
            if (refresh.RefreshCallCount == 0)
            {
                return Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                    "nyxid_catalog_snapshot_invalidated",
                    VisibleCatalogStateVersionBeforeRefresh));
            }

            if (FailureDetailAfterRefresh != null)
            {
                return Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    FailureDetailAfterRefresh,
                    VisibleCatalogStateVersionAfterRefresh));
            }

            if (RequiredExpiresAtUtcAfterRefresh.HasValue &&
                request.ExpiresAtUtc != RequiredExpiresAtUtcAfterRefresh.Value)
            {
                return Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    "authorization_plan_changed",
                    VisibleCatalogStateVersionAfterRefresh));
            }

            if (SuccessfulCatalogStateVersionAfterRefresh.HasValue)
            {
                var plan = RecordingAuthorizationPlanner.SuccessResult().Plan!.Clone();
                plan.CatalogAuthority.ActorStateVersion = SuccessfulCatalogStateVersionAfterRefresh.Value;
                return Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Succeeded(plan));
            }

            return VisibleCatalogStateVersionAfterRefresh < refresh.Result.StateVersion
                ? Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                    "nyxid_catalog_snapshot_stale",
                    VisibleCatalogStateVersionAfterRefresh))
                : Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Succeeded(
                    RecordingAuthorizationPlanner.SuccessResult().Plan!));
        }
    }

    private sealed record DeleteAttempt(
        bool OwnsEffectAttempt,
        bool NyxIdPending,
        bool VaultPending);

    private sealed record RichDeleteCall(
        string ScheduleId,
        TeamMemberAutomationOwner Owner,
        string OperationId,
        string IdempotencyKey,
        string Reason,
        ScheduledInvocationAuthorizationOwner AuthenticatedCredentialOwner);

    private sealed class RecordingCredentialMaterializer : IStudioScheduledCredentialMaterializer
    {
        public int MaterializeCallCount { get; private set; }
        public int RevokeCallCount { get; private set; }
        public string? BearerToken { get; private set; }
        public ScheduledInvocationAuthorizationPlan? Plan { get; private set; }
        public Aevatar.Foundation.Abstractions.OwnerScope? OwnerScope { get; private set; }
        public ScheduledCredentialEffectLocator? EffectLocator { get; private set; }
        public StudioScheduledCredential? Credential { get; init; }
        public Exception? MaterializeException { get; init; }
        public bool NyxIdRevoked { get; init; } = true;
        public bool VaultRevoked { get; init; } = true;
        public List<(string BearerToken, bool RevokeNyxId, bool RevokeVault)>
            RevocationCalls { get; } = [];
        public Queue<StudioScheduledCredentialRevocationResult>
            RevocationResults { get; } = [];

        public ScheduledCredentialEffectLocator CreateEffectLocator(
            string scheduleId,
            string operationId,
            ScheduledInvocationAuthorizationOwner credentialOwner) =>
            new(
                $"credential-{scheduleId}-{operationId}",
                $"secret-{scheduleId}-{operationId}",
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                $"schedule:{scheduleId}",
                credentialOwner);

        public Task<StudioScheduledCredential> MaterializeAsync(
            string bearerToken,
            ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
            string scheduleId,
            string operationId,
            ScheduledCredentialEffectLocator effectLocator,
            long effectAttemptGeneration,
            Aevatar.Foundation.Abstractions.OwnerScope ownerScope,
            CancellationToken ct = default)
        {
            MaterializeCallCount++;
            BearerToken = bearerToken;
            Plan = validatedPlan.Plan;
            OwnerScope = ownerScope;
            EffectLocator = effectLocator;
            if (MaterializeException != null)
                return Task.FromException<StudioScheduledCredential>(MaterializeException);
            return Task.FromResult(Credential ?? CreateCredential(
                TestNow.AddHours(20),
                CredentialSecretPurposes.ScheduledInvocationAgentKey));
        }

        public Task<StudioScheduledCredentialRevocationResult> RevokeAsync(
            string bearerToken,
            AuthenticatedAuthorizationOwnerContext authenticatedOwner,
            StudioScheduledCredential credential,
            bool revokeNyxId,
            bool revokeVault,
            CancellationToken ct = default)
        {
            RevokeCallCount++;
            RevocationCalls.Add((bearerToken, revokeNyxId, revokeVault));
            if (RevocationResults.Count > 0)
                return Task.FromResult(RevocationResults.Dequeue());
            return Task.FromResult(new StudioScheduledCredentialRevocationResult(
                NyxIdRevoked,
                VaultRevoked,
                ErrorCode: NyxIdRevoked && VaultRevoked ? string.Empty : "revocation_failed"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Math.Min(_index, values.Length - 1);
            _index++;
            return values[index];
        }
    }

    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public int EnsureCallCount { get; private set; }
        public int BeginCallCount { get; private set; }
        public int CandidateCallCount { get; private set; }
        public int FailCallCount { get; private set; }
        public int TombstonedAttempts { get; init; }
        public Exception? EnsureException { get; init; }
        public bool BeginOwnsEffectAttempt { get; init; } = true;
        public bool BeginNewOperationCommitted { get; init; } = true;
        public Exception? CandidateException { get; init; }
        public bool CommitCandidateBeforeException { get; init; }
        public bool ReturnPendingRevocationOnRetry { get; init; }
        public bool RetryOwnsEffectAttempt { get; init; } = true;
        public bool RejectMutationDigestDrift { get; init; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];
        public ScheduledDispatchDetail? TeamAutomationDetail { get; init; }
        public ScheduledDispatchListQuery? LastListQuery { get; private set; }
        public ScheduledDispatchListResult ListResult { get; init; } = new([], null, null);
        public TeamMemberAutomationOwner? LastTeamAutomationListOwner { get; private set; }
        public int? LastTeamAutomationListTake { get; private set; }
        public string? LastTeamAutomationListCursor { get; private set; }
        public bool? LastTeamAutomationListIncludeTotalCount { get; private set; }
        public ScheduledDispatchListResult TeamAutomationList { get; init; } = new([], null, null);
        public int UpdateCallCount { get; private set; }
        public int RetryRevocationCallCount { get; private set; }
        public int CompleteRevocationCallCount { get; private set; }
        public TeamAutomationCredentialOperation? BeginOperation { get; private set; }
        public List<string>? Calls { get; init; }
        public List<RichDeleteCall> RichDeleteCalls { get; } = [];
        public Queue<DeleteAttempt> DeleteAttempts { get; } = [];
        private ScheduledInvocationAgentKeyCredentialReference? _candidateCredential;
        private ScheduledInvocationAuthorizationOwner? _candidateOwner;
        private bool _candidateExceptionThrown;
        private string? _acceptedMutationDigest;

        public Task<TeamAutomationCommittedMutationReceipt> BeginTeamAutomationCredentialOperationAsync(
            TeamAutomationCredentialOperation operation,
            CancellationToken ct = default)
        {
            BeginCallCount++;
            Calls?.Add("begin");
            BeginOperation = operation;
            if (RejectMutationDigestDrift &&
                _acceptedMutationDigest != null &&
                !string.Equals(_acceptedMutationDigest, operation.MutationDigest, StringComparison.Ordinal))
            {
                throw new ScheduledDispatchConflictException(
                    operation.ScheduleId,
                    "team_automation_mutation_conflict");
            }
            _acceptedMutationDigest ??= operation.MutationDigest;
            return Task.FromResult(Committed(
                operation.ScheduleId,
                operation.OperationId,
                operation.IdempotencyKey,
                TeamAutomationOperationObservationStages.Begin,
                BeginOwnsEffectAttempt,
                "cmd-begin",
                effectAttemptId: BeginOwnsEffectAttempt ? "attempt-alpha" : string.Empty,
                candidateCredential: _candidateCredential,
                candidateOwner: _candidateOwner,
                credentialEffectLocator: operation.CredentialEffectLocator,
                newOperationCommitted: BeginNewOperationCommitted));
        }

        public Task<TeamAutomationCommittedMutationReceipt> RecordTeamAutomationCredentialCandidateAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            ScheduledInvocationAgentKeyCredentialReference credential,
            ScheduledInvocationAuthorizationOwner credentialOwner,
            CancellationToken ct = default)
        {
            CandidateCallCount++;
            if (CandidateException != null && !_candidateExceptionThrown)
            {
                _candidateExceptionThrown = true;
                if (CommitCandidateBeforeException)
                {
                    _candidateCredential = credential;
                    _candidateOwner = credentialOwner;
                }
                throw CandidateException;
            }
            _candidateCredential = credential;
            _candidateOwner = credentialOwner;
            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Candidate,
                ownsEffectAttempt: false,
                "cmd-candidate",
                candidateCredential: credential,
                candidateOwner: credentialOwner));
        }

        public Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationCredentialOperationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            ScheduledInvocationAgentKeyCredentialReference credential,
            ScheduledDispatchConfiguration configuration,
            CancellationToken ct = default)
        {
            EnsureCallCount++;
            Calls?.Add("complete");
            Configuration = configuration;
            Configurations.Add(configuration);
            if (EnsureCallCount <= TombstonedAttempts)
                throw new ScheduledDispatchNotFoundException(configuration.ScheduleId);
            if (EnsureException is not null)
                throw EnsureException;

            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Complete,
                ownsEffectAttempt: false,
                "cmd-complete"));
        }

        public Task<TeamAutomationCommittedMutationReceipt> FailTeamAutomationCredentialOperationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            string errorCode,
            CancellationToken ct = default)
        {
            FailCallCount++;
            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Fail,
                ownsEffectAttempt: false,
                "cmd-fail",
                errorCode));
        }

        public Task<TeamAutomationCommittedMutationReceipt> DeleteTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string reason,
            ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
            CancellationToken ct = default)
        {
            RichDeleteCalls.Add(new RichDeleteCall(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                reason,
                authenticatedCredentialOwner));
            var attempt = DeleteAttempts.Dequeue();
            var credential = CreateCredential(
                TestNow.AddHours(20),
                CredentialSecretPurposes.ScheduledInvocationAgentKey);
            var hasPendingRevocation = attempt.NyxIdPending || attempt.VaultPending;
            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Delete,
                attempt.OwnsEffectAttempt,
                "cmd-delete",
                effectAttemptId: attempt.OwnsEffectAttempt ? "attempt-delete" : string.Empty,
                pendingRevocationCredential: hasPendingRevocation
                    ? new ScheduledInvocationAgentKeyCredentialReference(
                        credential.SecretReference,
                        credential.ApiKeyId,
                        credential.ExpiresAtUtc.ToUnixTimeMilliseconds())
                    : null,
                pendingRevocationOwner: hasPendingRevocation ? credential.Owner : null,
                nyxIdRevocationPending: attempt.NyxIdPending,
                vaultRevocationPending: attempt.VaultPending));
        }

        public Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationRevocationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
            CancellationToken ct = default)
        {
            RetryRevocationCallCount++;
            var credential = CreateCredential(
                TestNow.AddHours(20),
                CredentialSecretPurposes.ScheduledInvocationAgentKey);
            return Task.FromResult(Committed(
                scheduleId,
                "operation-delete",
                "idempotency-delete",
                TeamAutomationOperationObservationStages.Delete,
                ownsEffectAttempt: ReturnPendingRevocationOnRetry && RetryOwnsEffectAttempt,
                "cmd-retry-revocation",
                effectAttemptId: ReturnPendingRevocationOnRetry && RetryOwnsEffectAttempt
                    ? "attempt-revocation"
                    : string.Empty,
                pendingRevocationCredential: ReturnPendingRevocationOnRetry
                    ? new ScheduledInvocationAgentKeyCredentialReference(
                        credential.SecretReference,
                        credential.ApiKeyId,
                        credential.ExpiresAtUtc.ToUnixTimeMilliseconds())
                    : null,
                pendingRevocationOwner: ReturnPendingRevocationOnRetry ? credential.Owner : null,
                nyxIdRevocationPending: ReturnPendingRevocationOnRetry,
                vaultRevocationPending: ReturnPendingRevocationOnRetry));
        }

        public Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationRevocationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            bool nyxIdRevoked,
            bool vaultRevoked,
            string errorCode,
            CancellationToken ct = default)
        {
            CompleteRevocationCallCount++;
            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                "idempotency-delete",
                TeamAutomationOperationObservationStages.Revocation,
                ownsEffectAttempt: false,
                "cmd-complete-revocation",
                errorCode,
                nyxIdRevocationPending: !nyxIdRevoked,
                vaultRevocationPending: !vaultRevoked));
        }

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("legacy_ensure_path_used");

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId, ScheduledDispatchConfiguration configuration, ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            UpdateCallCount++;
            Configuration = configuration;
            Configurations.Add(configuration);
            return Task.FromResult(Accepted(scheduleId, "cmd-update"));
        }

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
            ScheduledDispatchListQuery query, CancellationToken ct = default)
        {
            LastListQuery = query;
            return Task.FromResult(ListResult);
        }

        public Task<ScheduledDispatchListResult> ListTeamAutomationsAsync(
            TeamMemberAutomationOwner owner,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            LastTeamAutomationListOwner = owner;
            LastTeamAutomationListTake = take;
            LastTeamAutomationListCursor = cursor;
            LastTeamAutomationListIncludeTotalCount = includeTotalCount;
            return Task.FromResult(TeamAutomationList);
        }

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression, string? timezone, int count, DateTimeOffset? fromUtc = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(
            string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            CancellationToken ct = default) =>
            Task.FromResult(new ScheduledDispatchRunNowReceipt(
                scheduleId,
                $"scheduled-dispatch:{scheduleId}",
                TestNow,
                "backend-run-now-idempotency",
                Accepted: true,
                CommandId: "cmd-run-now",
                CorrelationId: "corr-run-now",
                AckedAt: TestNow,
                AckStage: "accepted"));

        public Task<ScheduledDispatchDetail?> GetTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            CancellationToken ct = default) =>
            Task.FromResult(TeamAutomationDetail);

        private static ScheduledDispatchMutationReceipt Accepted(string scheduleId, string commandId) =>
            new(
                scheduleId,
                $"scheduled-dispatch:{scheduleId}",
                Accepted: true,
                CommandId: commandId,
                CorrelationId: "corr-1",
                AckedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                AckStage: "accepted");

        private static TeamAutomationCommittedMutationReceipt Committed(
            string scheduleId,
            string operationId,
            string idempotencyKey,
            string stage,
            bool ownsEffectAttempt,
            string commandId,
            string errorCode = "",
            string effectAttemptId = "",
            ScheduledInvocationAgentKeyCredentialReference? candidateCredential = null,
            ScheduledInvocationAuthorizationOwner? candidateOwner = null,
            ScheduledCredentialEffectLocator? credentialEffectLocator = null,
            ScheduledInvocationAgentKeyCredentialReference? pendingRevocationCredential = null,
            ScheduledInvocationAuthorizationOwner? pendingRevocationOwner = null,
            bool nyxIdRevocationPending = false,
            bool vaultRevocationPending = false,
            bool newOperationCommitted = false) =>
            new(
                Accepted(scheduleId, commandId),
                new TeamAutomationOperationCommittedOutcome(
                    scheduleId,
                    operationId,
                    idempotencyKey,
                    stage,
                    ownsEffectAttempt,
                    StateVersion: 1,
                    errorCode,
                    ErrorMessage: string.Empty,
                    ObservedAtUtc: TestNow,
                    PendingRevocationCredential: pendingRevocationCredential,
                    PendingRevocationOwner: pendingRevocationOwner,
                    NyxIdRevocationPending: nyxIdRevocationPending,
                    VaultRevocationPending: vaultRevocationPending,
                    EffectAttemptId: effectAttemptId,
                    EffectAttemptGeneration: ownsEffectAttempt ? 1 : 0,
                    EffectAttemptExpiresAtUtc: ownsEffectAttempt ? TestNow.AddMinutes(5) : null,
                    CandidateCredential: candidateCredential,
                    CandidateOwner: candidateOwner,
                    CredentialEffectLocator: credentialEffectLocator,
                    NewOperationCommitted: newOperationCommitted));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<RecordedLogEntry> _entries = [];

        public IReadOnlyList<RecordedLogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category,
            List<RecordedLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var structuredState = state as IEnumerable<KeyValuePair<string, object?>>;
                entries.Add(new RecordedLogEntry(
                    category,
                    logLevel,
                    eventId,
                    structuredState?.ToArray() ?? [],
                    formatter(state, exception),
                    exception));
            }
        }
    }

    private sealed record RecordedLogEntry(
        string Category,
        LogLevel LogLevel,
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        string Message,
        Exception? Exception);
}
