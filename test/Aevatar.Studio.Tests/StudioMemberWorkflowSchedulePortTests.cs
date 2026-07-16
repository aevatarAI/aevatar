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
        var sut = NewPort(scheduleService, memberService);

        var result = await ScheduleAsync(sut, Request("scope-1", "member-1") with
        {
            Prompt = "run digest",
            DisplayName = "Daily digest",
        });

        result.Success.Should().BeTrue();
        result.Status.Should().Be("active");
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

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("snapshot_missing");
        scheduleService.EnsureCallCount.Should().Be(0);
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
    public async Task CreateAsync_WhenScheduleAdmissionFails_ShouldRevokeMaterializedCredential()
    {
        var scheduleService = new RecordingScheduleService { EnsureException = new InvalidOperationException("admission-failed") };
        var materializer = new RecordingCredentialMaterializer();
        var port = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(port, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("admission-failed");
        materializer.MaterializeCallCount.Should().Be(1);
        materializer.RevokeCallCount.Should().Be(1);
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
        materializer.RevokeCallCount.Should().Be(1);
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
    public async Task CreateAsync_WhenScheduleIsTombstoned_ShouldRevokeCredentialAfterFirstAttempt()
    {
        var scheduleService = new RecordingScheduleService { TombstonedAttempts = 50 };
        var materializer = new RecordingCredentialMaterializer();
        var sut = NewPort(scheduleService, materializer: materializer);

        var action = () => ScheduleAsync(sut, Request("scope-1", "member-1"));

        await action.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        scheduleService.EnsureCallCount.Should().Be(1);
        scheduleService.Configurations.Should().ContainSingle();
        materializer.RevokeCallCount.Should().Be(1);
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
        IStudioScheduledCredentialMaterializer? materializer = null)
    {
        var resolvedPlanner = planner ?? new RecordingAuthorizationPlanner();
        return new StudioMemberWorkflowSchedulePort(
            memberService ?? new RecordingMemberService { Detail = CreateWorkflowMemberDetail() },
            schedule,
            resolvedPlanner,
            new RecordingAuthorizationRevalidator(resolvedPlanner),
            materializer ?? new RecordingCredentialMaterializer(),
            new FixedTimeProvider(TestNow));
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
        public ScheduledInvocationAuthorizationPlanResult Result { get; init; } =
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
            return Task.FromResult(Result);
        }
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
                return ScheduledInvocationAuthorizationValidationResult.Failed(result.FailureCode, result.Detail);
            var plan = result.Plan!;
            return string.Equals(confirmation.PermissionDigest, plan.PermissionDigest, StringComparison.Ordinal) &&
                   string.Equals(confirmation.PolicyVersion, plan.CredentialPolicy.PolicyVersion, StringComparison.Ordinal)
                ? ScheduledInvocationAuthorizationValidationResult.Succeeded(plan)
                : ScheduledInvocationAuthorizationValidationResult.Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    "authorization_plan_changed");
        }
    }

    private sealed class RecordingCredentialMaterializer : IStudioScheduledCredentialMaterializer
    {
        public int MaterializeCallCount { get; private set; }
        public int RevokeCallCount { get; private set; }
        public string? BearerToken { get; private set; }
        public ScheduledInvocationAuthorizationPlan? Plan { get; private set; }
        public Aevatar.Foundation.Abstractions.OwnerScope? OwnerScope { get; private set; }
        public StudioScheduledCredential? Credential { get; init; }

        public Task<StudioScheduledCredential> MaterializeAsync(
            string bearerToken,
            ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
            string scheduleId,
            Aevatar.Foundation.Abstractions.OwnerScope ownerScope,
            CancellationToken ct = default)
        {
            MaterializeCallCount++;
            BearerToken = bearerToken;
            Plan = validatedPlan.Plan;
            OwnerScope = ownerScope;
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

    private sealed class RecordingScheduleService : IScheduledDispatchApplicationService
    {
        public int EnsureCallCount { get; private set; }
        public int BeginCallCount { get; private set; }
        public int FailCallCount { get; private set; }
        public int TombstonedAttempts { get; init; }
        public Exception? EnsureException { get; init; }
        public bool BeginOwnsEffectAttempt { get; init; } = true;
        public ScheduledDispatchConfiguration? Configuration { get; private set; }
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];

        public Task<TeamAutomationCommittedMutationReceipt> BeginTeamAutomationCredentialOperationAsync(
            TeamAutomationCredentialOperation operation,
            CancellationToken ct = default)
        {
            BeginCallCount++;
            return Task.FromResult(Committed(
                operation.ScheduleId,
                operation.OperationId,
                operation.IdempotencyKey,
                TeamAutomationOperationObservationStages.Begin,
                BeginOwnsEffectAttempt,
                "cmd-begin"));
        }

        public Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationCredentialOperationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            ScheduledInvocationAgentKeyCredentialReference credential,
            ScheduledDispatchConfiguration configuration,
            CancellationToken ct = default)
        {
            EnsureCallCount++;
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

        public Task<ScheduledDispatchDetail?> GetTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            CancellationToken ct = default) =>
            Task.FromResult<ScheduledDispatchDetail?>(null);

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
            string errorCode = "") =>
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
                    PendingRevocationCredential: null,
                    PendingRevocationOwner: null,
                    NyxIdRevocationPending: false,
                    VaultRevocationPending: false));
    }
}
