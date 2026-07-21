using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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
        invocation.AuthorizationFact.Should().NotBeNull();
        var fact = invocation.AuthorizationFact!;
        fact.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        fact.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        fact.ServiceGrants.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledInvocationAuthorizationServiceGrant("nyx-service-alpha", ["nyx-node-alpha"], false));
        fact.Authority.Should().BeEquivalentTo(new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationAuthority(
            3, 5, 7, 11, 13,
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            "catalog-revision-alpha", "catalog-digest-alpha"));
    }

    [Fact]
    public async Task EnsureAsync_UsesMaterializedScheduledCredentialAndStableMemberOwner()
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
        auth!.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey!.ApiKeyId.Should().Be("key-alpha");
        auth.ScheduledInvocationAgentKey.SecretReference.Ref.Should().Be("secret-alpha");
        auth.ScheduledInvocationAgentKey.SecretReference.Purpose.Should()
            .Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        auth.SenderNyxId.Should().BeNull();
        auth.Durable.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
        scheduleService.Configuration.TeamAutomationOwner.Should()
            .Be(new TeamMemberAutomationOwner("scope-1", "member-1"));
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
    public async Task PreflightAsync_WhenCatalogSnapshotMissingAndBearerAvailable_ShouldRefreshSameOwnerAndRetryOnce()
    {
        var planner = new RecordingAuthorizationPlanner();
        planner.Results.Enqueue(ScheduledInvocationAuthorizationPlanResult.Failed(
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
            "nyxid_catalog_snapshot_not_found"));
        planner.Results.Enqueue(RecordingAuthorizationPlanner.SuccessResult());
        var refresh = new RecordingCatalogRefreshPort();
        var request = Request("scope-1", "member-1");
        var retryNow = TestNow.AddSeconds(3);
        var port = NewPort(
            new RecordingScheduleService(),
            planner: planner,
            catalogRefresh: refresh,
            timeProvider: new SequenceTimeProvider(TestNow, retryNow));

        var result = await port.PreflightAsync(request);

        result.Success.Should().BeTrue();
        result.Plan!.PermissionDigest.Should().Be(RecordingAuthorizationPlanner.Digest);
        planner.Requests.Should().HaveCount(2);
        planner.Requests[0].EvaluatedAtUtc.Should().Be(TestNow);
        planner.Requests[1].EvaluatedAtUtc.Should().Be(retryNow);
        planner.Requests[1].ExpiresAtUtc.Should().Be(
            new StudioMemberWorkflowSchedulePolicy().ResolveCredentialExpiresAtUtc(retryNow));
        refresh.RefreshCallCount.Should().Be(1);
        refresh.LastOwner.Should().BeEquivalentTo(request.AuthenticatedOwner.Owner);
        refresh.LastBearerToken.Should().Be("bearer-alpha");
    }

    [Fact]
    public async Task PreflightAsync_WhenCatalogSnapshotStaleWithoutBearer_ShouldReturnActionableFailureAndNotRefresh()
    {
        var planner = new RecordingAuthorizationPlanner
        {
            Result = ScheduledInvocationAuthorizationPlanResult.Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                "nyxid_catalog_snapshot_stale"),
        };
        var refresh = new RecordingCatalogRefreshPort();
        var port = NewPort(new RecordingScheduleService(), planner: planner, catalogRefresh: refresh);

        var result = await port.PreflightAsync(Request("scope-1", "member-1") with
        {
            ProvisioningBearerToken = null,
        });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
        result.Detail.Should().Be("nyxid_catalog_refresh_requires_bearer_token:nyxid_catalog_snapshot_stale");
        refresh.RefreshCallCount.Should().Be(0);
        planner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PreflightAsync_WhenCatalogRefreshDoesNotObserveSnapshot_ShouldReturnRefreshFailureDetail()
    {
        var planner = new RecordingAuthorizationPlanner
        {
            Result = ScheduledInvocationAuthorizationPlanResult.Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_snapshot_invalidated"),
        };
        var refresh = new RecordingCatalogRefreshPort
        {
            Result = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.PublishedContractMissing,
                "nyxid_catalog_published_contract_missing"),
        };
        var port = NewPort(new RecordingScheduleService(), planner: planner, catalogRefresh: refresh);

        var result = await port.PreflightAsync(Request("scope-1", "member-1"));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound);
        result.Detail.Should().Be("nyxid_catalog_refresh_failed:nyxid_catalog_published_contract_missing");
        refresh.RefreshCallCount.Should().Be(1);
        planner.Requests.Should().ContainSingle();
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
    public void ToScheduleAuthorizationFact_ShouldMapMixedDirectAndNodeBackedServicesPerService()
    {
        var plan = new RecordingAuthorizationPlanner().Result.Plan!.Clone();
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant { UserServiceId = "nyx-service-direct" });

        var fact = StudioMemberWorkflowSchedulePort.ToScheduleAuthorizationFact(plan);

        fact.ServiceGrants.Should().HaveCount(2);
        fact.ServiceGrants[0].ServiceId.Should().Be("nyx-service-alpha");
        fact.ServiceGrants[0].NodeIds.Should().Equal("nyx-node-alpha");
        fact.ServiceGrants[0].NodeGrantsNotRequired.Should().BeFalse();
        fact.ServiceGrants[1].ServiceId.Should().Be("nyx-service-direct");
        fact.ServiceGrants[1].NodeIds.Should().BeEmpty();
        fact.ServiceGrants[1].NodeGrantsNotRequired.Should().BeTrue();
        fact.NodeGrants.Should().ContainSingle(node =>
            node.UserServiceId == "nyx-service-alpha" &&
            node.NodeId == "nyx-node-alpha");
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
    public async Task CreateAsync_WhenBeginReplayDoesNotOwnEffect_ShouldNotMaterializeCredential()
    {
        var scheduleService = new RecordingScheduleService { BeginOwnsEffectAttempt = false };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);

        var result = await ScheduleAsync(port, Request("scope-1", "member-1"));

        result.Status.Should().Be("pending");
        scheduleService.BeginCallCount.Should().Be(1);
        scheduleService.EnsureCallCount.Should().Be(0);
        materializer.MaterializeCallCount.Should().Be(0);
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
    public async Task RetryRevocationAsync_ShouldUseIdentityOnlyCommandAndExecuteCommittedEffects()
    {
        var scheduleService = new RecordingScheduleService { ReturnPendingRevocationOnRetry = true };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);
        var request = Request("scope-1", "member-1");
        var command = new StudioMemberAutomationActionCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "operation-delete",
            "idempotency-delete")
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
    public async Task ReauthorizeAsync_WhenPermissionDigestChanged_ShouldNotDispatch()
    {
        var scheduleService = new RecordingScheduleService();
        var port = NewPort(scheduleService);

        var action = () => port.ReauthorizeAsync(Request("scope-1", "member-1"), "stale-digest");

        await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage("authorization_plan_changed");
        scheduleService.EnsureCallCount.Should().Be(0);
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
    public async Task CreateAsync_ShouldDigestNormalizedSemanticMutationWithoutCredentialMaterial()
    {
        var first = new RecordingScheduleService();
        var replay = new RecordingScheduleService();
        var drifted = new RecordingScheduleService();
        var request = Request("scope-1", "member-1") with
        {
            DisplayName = " Daily digest ",
            Prompt = " summarize ",
        };

        await ScheduleAsync(NewPort(first), request);
        await ScheduleAsync(NewPort(replay), request);
        await ScheduleAsync(NewPort(drifted), request with { Prompt = "summarize something else" });

        first.BeginOperation!.MutationDigest.Should().MatchRegex("^[a-f0-9]{64}$");
        replay.BeginOperation!.MutationDigest.Should().Be(first.BeginOperation.MutationDigest);
        drifted.BeginOperation!.MutationDigest.Should().NotBe(first.BeginOperation.MutationDigest);
        first.BeginOperation.CredentialEffectLocator.CredentialOwner.Should().Be(
            new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "nyx-owner-alpha"));
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
                "binding-alpha"))
        {
            TeamId = "team-1",
            OperationId = "operation-alpha",
            IdempotencyKey = "idempotency-alpha",
            ProvisioningBearerToken = "bearer-alpha",
            CredentialProvisioningKind = "dedicated_scheduled_invocation_agent_key",
            ConfirmedPolicyVersion = RecordingAuthorizationPlanner.PolicyVersion,
        };

    private static StudioMemberWorkflowSchedulePort NewPort(
        RecordingScheduleService schedule,
        RecordingMemberService? memberService = null,
        IScheduledInvocationAuthorizationPlanner? planner = null,
        IScheduledInvocationAuthorizationRevalidator? revalidator = null,
        IStudioScheduledCredentialMaterializer? materializer = null,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefresh = null,
        TimeProvider? timeProvider = null)
    {
        var resolvedPlanner = planner ?? new RecordingAuthorizationPlanner();
        return new StudioMemberWorkflowSchedulePort(
            memberService ?? new RecordingMemberService { Detail = CreateWorkflowMemberDetail() },
            schedule,
            resolvedPlanner,
            revalidator ?? new RecordingAuthorizationRevalidator(resolvedPlanner),
            materializer ?? new RecordingCredentialMaterializer(),
            timeProvider ?? new FixedTimeProvider(TestNow),
            catalogRefresh);
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
                    ExternalRevision = "catalog-revision-alpha",
                    ContentDigest = "catalog-digest-alpha",
                },
            };
            plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant { UserServiceId = "nyx-service-alpha" });
            plan.NyxIdNodeGrants.Add(new NyxIdNodeGrant
            {
                UserServiceId = "nyx-service-alpha",
                NodeId = "nyx-node-alpha",
                Role = NyxIdNodeRole.Primary,
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

    private sealed class RecordingCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
    {
        public int RefreshCallCount { get; private set; }
        public AuthorizationOwnerIdentity? LastOwner { get; private set; }
        public string? LastBearerToken { get; private set; }
        public List<string>? Calls { get; init; }
        public NyxIdAuthorizationCatalogRefreshResult Result { get; init; } =
            NyxIdAuthorizationCatalogRefreshResult.Observed;

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            CancellationToken ct = default)
        {
            RefreshCallCount++;
            Calls?.Add("refresh");
            LastOwner = owner.Clone();
            LastBearerToken = bearerToken;
            return Task.FromResult(Result);
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
                    "nyxid_catalog_snapshot_invalidated"));
            }

            return Task.FromResult(ScheduledInvocationAuthorizationValidationResult.Succeeded(
                RecordingAuthorizationPlanner.SuccessResult().Plan!));
        }
    }

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
            return Task.FromResult(new StudioScheduledCredentialRevocationResult(
                NyxIdRevoked: true,
                VaultRevoked: true,
                ErrorCode: string.Empty));
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
        public Exception? CandidateException { get; init; }
        public bool CommitCandidateBeforeException { get; init; }
        public bool ReturnPendingRevocationOnRetry { get; init; }
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];
        public ScheduledDispatchDetail? TeamAutomationDetail { get; init; }
        public int UpdateCallCount { get; private set; }
        public int RetryRevocationCallCount { get; private set; }
        public int CompleteRevocationCallCount { get; private set; }
        public TeamAutomationCredentialOperation? BeginOperation { get; private set; }
        public List<string>? Calls { get; init; }
        private ScheduledInvocationAgentKeyCredentialReference? _candidateCredential;
        private ScheduledInvocationAuthorizationOwner? _candidateOwner;
        private bool _candidateExceptionThrown;

        public Task<TeamAutomationCommittedMutationReceipt> BeginTeamAutomationCredentialOperationAsync(
            TeamAutomationCredentialOperation operation,
            CancellationToken ct = default)
        {
            BeginCallCount++;
            Calls?.Add("begin");
            BeginOperation = operation;
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
                credentialEffectLocator: operation.CredentialEffectLocator));
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

        public Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationRevocationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
            CancellationToken ct = default)
        {
            RetryRevocationCallCount++;
            var credential = CreateCredential(
                TestNow.AddHours(20),
                CredentialSecretPurposes.ScheduledInvocationAgentKey);
            return Task.FromResult(Committed(
                scheduleId,
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Delete,
                ownsEffectAttempt: ReturnPendingRevocationOnRetry,
                "cmd-retry-revocation",
                effectAttemptId: ReturnPendingRevocationOnRetry ? "attempt-revocation" : string.Empty,
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
                "cmd-complete-revocation"));
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
            ScheduledDispatchListQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression, string? timezone, int count, DateTimeOffset? fromUtc = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(
            string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
            bool vaultRevocationPending = false) =>
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
                    CredentialEffectLocator: credentialEffectLocator));
    }
}
