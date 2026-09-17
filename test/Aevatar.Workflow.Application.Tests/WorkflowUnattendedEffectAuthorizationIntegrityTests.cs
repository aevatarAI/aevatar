using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowUnattendedEffectAuthorizationIntegrityTests
{
    private const string DefinitionActorId = "definition-alpha";
    private const string ScopeId = "scope-alpha";
    private const string WorkflowId = "workflow-alpha";
    private const string RevisionId = "revision-alpha";
    private const string RouteKey = "hr/approval-created";
    private const string GrantorSubject = "owner-alpha";
    private const string CallSiteId = "workflow-alpha/create-approval";
    private const long DefinitionVersion = 17;

    [Fact]
    public void CreateAndValidatorsAndPermit_ShouldAcceptExactDurableAuthoredRequest()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();

        var authorization = CreateAuthorization(plan, authority);

        authorization.DefinitionActorId.Should().Be(DefinitionActorId);
        authorization.ScopeId.Should().Be(ScopeId);
        authorization.WorkflowId.Should().Be(WorkflowId);
        authorization.RevisionId.Should().Be(RevisionId);
        authorization.DefinitionVersion.Should().Be(DefinitionVersion);
        authorization.AdmissionDigest.Should().Be(plan.AdmissionDigest);
        authorization.Invocations.Should().ContainSingle();

        var validateDefinition = () => ValidateForDefinition(authorization, authority, plan);
        var validateActorState = () => ValidateForActorState(authorization, authority, plan);

        validateDefinition.Should().NotThrow();
        validateActorState.Should().NotThrow();

        var admission = plan.InvocationAdmissions.Should().ContainSingle().Subject;
        var permit = WorkflowUnattendedEffectAuthorizationIntegrity.CreateInvocationPermit(
            authorization,
            authority,
            admission);

        permit.Should().NotBeNull();
        permit!.AuthorizationId.Should().Be(authorization.AuthorizationDigest);
        permit.CallSiteId.Should().Be(CallSiteId);
        permit.CapabilityContractDigest.Should().Be(
            admission.Capability.NyxIdUserRequest.ContractDigest);
        permit.ExplicitRequestGrantDigest.Should().Be(
            admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest);
    }

    [Fact]
    public void ValidateForDefinition_ShouldFailClosed_WhenRouteDrifts()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        var authorization = CreateAuthorization(plan, authority);

        var act = () => WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForDefinition(
            authorization,
            authority,
            "hr/other-route",
            DefinitionActorId,
            ScopeId,
            WorkflowId,
            RevisionId,
            DefinitionVersion,
            plan);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateForActorState_ShouldFailClosed_WhenDefinitionVersionDrifts()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        var authorization = CreateAuthorization(plan, authority);

        var act = () => WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForActorState(
            authorization,
            authority,
            DefinitionActorId,
            ScopeId,
            WorkflowId,
            RevisionId,
            DefinitionVersion + 1,
            plan);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateForActorState_ShouldFailClosed_WhenAdmissionDigestDrifts()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        var authorization = CreateAuthorization(plan, authority);
        var driftedPlan = plan.Clone();
        driftedPlan.SourceStamps[0].ContentDigest = "user-services-digest-beta";
        driftedPlan.AdmissionDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(driftedPlan);

        driftedPlan.AdmissionDigest.Should().NotBe(plan.AdmissionDigest);
        var act = () => ValidateForActorState(authorization, authority, driftedPlan);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidationAndPermit_ShouldFailClosed_WhenAuthorityBindingDrifts()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        var authorization = CreateAuthorization(plan, authority);
        var driftedAuthority = authority.Clone();
        driftedAuthority.BindingId = "binding-beta";

        var validate = () => ValidateForActorState(authorization, driftedAuthority, plan);
        var permit = WorkflowUnattendedEffectAuthorizationIntegrity.CreateInvocationPermit(
            authorization,
            driftedAuthority,
            plan.InvocationAdmissions.Single());

        validate.Should().Throw<InvalidOperationException>();
        permit.Should().BeNull();
    }

    [Fact]
    public void CreateAndValidation_ShouldFailClosed_WhenCallerSubjectDoesNotOwnDurablePlan()
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        authority.ExternalUserId = "other-owner";

        var create = () => CreateAuthorization(plan, authority);

        create.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("owner_kind")]
    public void Create_ShouldFailClosed_WhenExactGrantOwnerDriftsFromDurableOwner(string mutation)
    {
        var plan = CreatePlan();
        var admission = plan.InvocationAdmissions.Single();
        switch (mutation)
        {
            case "subject":
                admission.NyxIdExplicitRequestGrant.GrantorOwnerSubject = "other-owner";
                break;
            case "owner_kind":
                admission.NyxIdExplicitRequestGrant.GrantorOwnerKind =
                    ExternalCapabilityAuthorizationOwnerKind.Organization;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(
                admission.NyxIdExplicitRequestGrant);
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        var create = () => CreateAuthorization(plan, CreateAuthority());

        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_ShouldFailClosed_WhenPlanContainsOnlyDestructiveAdmission()
    {
        var destructivePlan = CreatePlan(
            NyxIdRequestMethod.Delete,
            NyxIdOperationRisk.Destructive);

        var act = () => CreateAuthorization(destructivePlan, CreateAuthority());

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("call_site")]
    [InlineData("contract_digest")]
    [InlineData("destructive")]
    public void CreateInvocationPermit_ShouldFailClosed_WhenAdmissionIsNotExact(string mutation)
    {
        var plan = CreatePlan();
        var authority = CreateAuthority();
        var authorization = CreateAuthorization(plan, authority);
        var admission = plan.InvocationAdmissions.Single().Clone();

        switch (mutation)
        {
            case "call_site":
                admission.CallSiteId = "workflow-alpha/other-call";
                break;
            case "contract_digest":
                admission.Capability.NyxIdUserRequest.ContractDigest = "sha256:other-contract";
                break;
            case "destructive":
                admission.Capability.NyxIdUserRequest.ExecutionPolicy.Risk =
                    NyxIdOperationRisk.Destructive;
                admission.NyxIdExplicitRequestGrant.Risk = NyxIdOperationRisk.Destructive;
                admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
                    WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(
                        admission.NyxIdExplicitRequestGrant);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var permit = WorkflowUnattendedEffectAuthorizationIntegrity.CreateInvocationPermit(
            authorization,
            authority,
            admission);

        permit.Should().BeNull();
    }

    private static WorkflowUnattendedEffectAuthorization CreateAuthorization(
        WorkflowCapabilityAdmissionPlan plan,
        WorkflowCallerNyxIdAuthority authority) =>
        WorkflowUnattendedEffectAuthorizationIntegrity.Create(
            DefinitionActorId,
            ScopeId,
            WorkflowId,
            RevisionId,
            RouteKey,
            GrantorSubject,
            DefinitionVersion,
            authority,
            plan);

    private static void ValidateForDefinition(
        WorkflowUnattendedEffectAuthorization authorization,
        WorkflowCallerNyxIdAuthority authority,
        WorkflowCapabilityAdmissionPlan plan) =>
        WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForDefinition(
            authorization,
            authority,
            RouteKey,
            DefinitionActorId,
            ScopeId,
            WorkflowId,
            RevisionId,
            DefinitionVersion,
            plan);

    private static void ValidateForActorState(
        WorkflowUnattendedEffectAuthorization authorization,
        WorkflowCallerNyxIdAuthority authority,
        WorkflowCapabilityAdmissionPlan plan) =>
        WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForActorState(
            authorization,
            authority,
            DefinitionActorId,
            ScopeId,
            WorkflowId,
            RevisionId,
            DefinitionVersion,
            plan);

    private static WorkflowCallerNyxIdAuthority CreateAuthority() => new()
    {
        Platform = "lark",
        Tenant = "tenant-alpha",
        ExternalUserId = GrantorSubject,
        Scope = "personal",
        BindingId = "binding-alpha",
    };

    private static WorkflowCapabilityAdmissionPlan CreatePlan(
        NyxIdRequestMethod method = NyxIdRequestMethod.Post,
        NyxIdOperationRisk risk = NyxIdOperationRisk.Write)
    {
        var admission = CreateAdmission(method, risk);
        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: workflow-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [admission],
            [
                CreateSource(ExternalCapabilitySourceKind.NyxIdUserServices, "user-services-alpha"),
                CreateSource(
                    ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                    "durable-authorization-alpha",
                    sourceVersion: 7),
            ],
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = GrantorSubject,
            },
            WorkflowId,
            RevisionId);
    }

    private static WorkflowCapabilityInvocationAdmission CreateAdmission(
        NyxIdRequestMethod method,
        NyxIdOperationRisk risk)
    {
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "user-service-alpha",
            Method = method,
            PathTemplate = "/open-apis/approval/v4/instances",
            BodyMode = method == NyxIdRequestMethod.Delete
                ? NyxIdRequestBodyMode.None
                : NyxIdRequestBodyMode.Json,
            BodyRequired = method != NyxIdRequestMethod.Delete,
            ResponseMode = NyxIdRequestResponseMode.Text,
            Risk = risk,
        };
        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = new NyxIdExplicitRequestGrant
        {
            CallSiteId = CallSiteId,
            RequestContractDigest = requestDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = GrantorSubject,
            Risk = risk,
            WorkflowId = WorkflowId,
            RevisionId = RevisionId,
        };
        grant.AllowedExecutionModes.Add([
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable,
        ]);
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = risk,
            Approval = NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(grant.AllowedExecutionModes);
        var proof = new NyxIdUserRequestCapabilityRef
        {
            Request = request,
            ServiceSlugSnapshot = "lark",
            ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "lark"),
            ExecutionPolicy = policy,
            ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdExplicitRequestGrantDigest(grant),
        };
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = CallSiteId,
            Capability = new ExternalWorkflowCapabilityRef { NyxIdUserRequest = proof },
            NyxIdExplicitRequestGrant = grant,
        };
    }

    private static ExternalCapabilitySourceStamp CreateSource(
        ExternalCapabilitySourceKind sourceKind,
        string sourceId,
        long sourceVersion = 0) =>
        new()
        {
            SourceKind = sourceKind,
            SourceId = sourceId,
            SourceVersion = sourceVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)),
            FreshUntil = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 13, 0, 5, 0, TimeSpan.Zero)),
            ContentDigest = $"{sourceId}-digest",
        };
}
