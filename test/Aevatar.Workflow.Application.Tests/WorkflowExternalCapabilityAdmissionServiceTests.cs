using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExternalCapabilityAdmissionServiceTests
{
    [Fact]
    public void AdmissionPlanContract_ShouldUseV6ResponseProjectionAdmissionsAsTheOnlyCurrentFactSource()
    {
        WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion.Should()
            .Be("external-capability-admission.v6");
        WorkflowCapabilityAdmissionPlanIntegrity.CodeRouteSchemaVersion.Should()
            .Be("external-capability-admission.v5");

        var create = typeof(WorkflowCapabilityAdmissionPlanIntegrity)
            .GetMethods()
            .Single(method => method.Name == nameof(WorkflowCapabilityAdmissionPlanIntegrity.Create));
        create.GetParameters()[3].ParameterType.Should()
            .Be(typeof(IEnumerable<WorkflowCapabilityInvocationAdmission>));
    }

    [Fact]
    public void NyxIdSelectorAndProof_ShouldUseEndpointIdentityFields()
    {
        NyxIdOperationSelector.Descriptor.FindFieldByName("endpoint_id").Should().NotBeNull();
        NyxIdOperationSelector.Descriptor.FindFieldByName("operation_id").Should().BeNull();
        NyxIdUserServiceCapabilityRef.Descriptor.FindFieldByName("endpoint_id").Should().NotBeNull();
        NyxIdUserServiceCapabilityRef.Descriptor.FindFieldByName("operation_id").Should().BeNull();
    }

    [Fact]
    public void ReadinessContract_ShouldInspectAnAuthorSelectorAndReturnAServerProof()
    {
        var requestConstructor = typeof(InspectExternalWorkflowCapabilityReadinessRequest)
            .GetConstructors()
            .Should().ContainSingle().Subject;

        requestConstructor.GetParameters()[1].ParameterType.Should()
            .Be(typeof(ExternalWorkflowCapabilitySelector));
        typeof(ExternalCapabilityReadiness).GetProperty(nameof(ExternalCapabilityReadiness.SelectedCapability))
            .Should().NotBeNull();
    }

    [Fact]
    public void AdmissionPlanContract_ShouldCarryTypedDurableAuthorizationOwner()
    {
        var field = WorkflowCapabilityAdmissionPlan.Descriptor.FindFieldByName(
            "durable_authorization_owner");

        field.Should().NotBeNull();
        field!.MessageType.Name.Should().Be("ExternalCapabilityAuthorizationOwner");
    }

    [Fact]
    public void AdmissionPlanContract_ShouldCarryTypedExplicitRequestGrant()
    {
        var bodyRequired = NyxIdRequestSelector.Descriptor.FindFieldByName("body_required");
        var grant = WorkflowCapabilityInvocationAdmission.Descriptor.FindFieldByName(
            "nyx_id_explicit_request_grant");

        bodyRequired.Should().NotBeNull();
        bodyRequired!.FieldType.Should().Be(Google.Protobuf.Reflection.FieldType.Bool);
        grant.Should().NotBeNull();
        grant!.MessageType.Name.Should().Be("NyxIdExplicitRequestGrant");
        grant.MessageType.FindFieldByName("call_site_id").Should().NotBeNull();
        grant.MessageType.FindFieldByName("request_contract_digest").Should().NotBeNull();
        grant.MessageType.FindFieldByName("grantor_authority")!.EnumType.Name.Should()
            .Be("NyxIdExplicitRequestGrantorAuthority");
        grant.MessageType.FindFieldByName("grantor_owner_kind")!.EnumType.Name.Should()
            .Be("ExternalCapabilityAuthorizationOwnerKind");
        grant.MessageType.FindFieldByName("grantor_owner_subject").Should().NotBeNull();
        grant.MessageType.FindFieldByName("risk")!.EnumType.Name.Should()
            .Be("NyxIdOperationRisk");
        grant.MessageType.FindFieldByName("allowed_execution_modes")!.EnumType.Name.Should()
            .Be("ExternalCapabilityExecutionMode");
        NyxIdUserRequestCapabilityRef.Descriptor.FindFieldByName("explicit_request_grant_digest")
            .Should().NotBeNull();
    }

    [Fact]
    public void NyxIdProofPolicy_ShouldParticipateInTheExistingAdmissionDigest()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var readOnly = NyxIdCapability();
        var write = readOnly.Clone();
        write.NyxIdUserService.ExecutionPolicy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.Write,
            Approval = NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
        };

        var readPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(readOnly),
            Ready(readOnly).Sources);
        var writePlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(write),
            Ready(write).Sources);

        writePlan.AdmissionDigest.Should().NotBe(readPlan.AdmissionDigest);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("risk_unspecified")]
    [InlineData("risk_unknown")]
    [InlineData("approval_mismatch")]
    [InlineData("owner_unspecified")]
    [InlineData("modes_empty")]
    [InlineData("modes_duplicate")]
    [InlineData("modes_without_interactive")]
    [InlineData("modes_unknown")]
    public void Create_ShouldRejectMalformedNyxIdExecutionPolicy(string malformedCase)
    {
        var capability = NyxIdCapability();
        var policy = capability.NyxIdUserService.ExecutionPolicy;
        switch (malformedCase)
        {
            case "missing":
                capability.NyxIdUserService.ExecutionPolicy = null;
                break;
            case "risk_unspecified":
                policy.Risk = NyxIdOperationRisk.Unspecified;
                break;
            case "risk_unknown":
                policy.Risk = (NyxIdOperationRisk)99;
                break;
            case "approval_mismatch":
                policy.Approval = NyxIdOperationApproval.Required;
                break;
            case "owner_unspecified":
                policy.EnforcementOwner = NyxIdOperationEnforcementOwner.Unspecified;
                break;
            case "modes_empty":
                policy.AllowedExecutionModes.Clear();
                break;
            case "modes_duplicate":
                policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
                break;
            case "modes_without_interactive":
                policy.AllowedExecutionModes.Clear();
                policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
                break;
            case "modes_unknown":
                policy.AllowedExecutionModes.Add((ExternalCapabilityExecutionMode)99);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(malformedCase));
        }

        Action act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(capability),
            Ready(capability).Sources);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution policy*");
    }

    [Fact]
    public void Create_ShouldRejectExecutionModeMissingFromNyxIdPolicy()
    {
        var capability = NyxIdCapability();
        capability.NyxIdUserService.ExecutionPolicy.AllowedExecutionModes.Clear();
        capability.NyxIdUserService.ExecutionPolicy.AllowedExecutionModes.Add(
            ExternalCapabilityExecutionMode.Interactive);

        Action act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(capability).Sources);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*execution policy*");
    }

    [Fact]
    public void AdmissionPlanContract_ShouldPreserveTheLegacyV2WireFixtureWhenV3FieldsAreEmpty()
    {
        const string legacyWireBase64 =
            "CiBleHRlcm5hbC1jYXBhYmlsaXR5LWFkbWlzc2lvbi52MhIRZGVmaW5pdGlvbi1kaWdlc3QYATJAMzg5ODkwYzIzMmI5NGEzNzU0MDQ4MjE4NGZhMDVhYjhmYjJiZjg2NTcwOTNhNmM1ODgyYThkMDM3YjE1MGFhNg==";
        var legacyWire = Convert.FromBase64String(legacyWireBase64);
        var plan = WorkflowCapabilityAdmissionPlan.Parser.ParseFrom(legacyWire);

        plan.InvocationAdmissions.Should().BeEmpty();
        plan.ToByteArray().Should().Equal(legacyWire);
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));

        var v3 = plan.Clone();
        v3.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion;
        WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(v3).Should()
            .NotBe(plan.AdmissionDigest);
    }

    [Fact]
    public void AdmissionServiceContract_ShouldSeparatePersistedPlanRevalidation()
    {
        typeof(IWorkflowExternalCapabilityAdmissionService)
            .GetMethod("RevalidatePersistedAsync")
            .Should().NotBeNull();
    }

    [Fact]
    public void AdmissionRequest_ShouldExposeOrderedWorkflowBundleFactory()
    {
        typeof(WorkflowExternalCapabilityAdmissionRequest)
            .GetMethod("FromWorkflowYamls")
            .Should().NotBeNull();
    }

    [Fact]
    public void PersistedAdmissionRequest_ShouldRequireExpectedExecutionMode()
    {
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        var act = () => new PersistedWorkflowCapabilityAdmissionRequest(
            plan,
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            "scope-workflow",
            ExternalCapabilityExecutionMode.Unspecified);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode is required*");
    }

    [Fact]
    public void AdmissionPlanIntegrity_ShouldAcceptHostConnectorSelectorWithoutOptionalContractDigest()
    {
        const string yaml = "name: connector-workflow\nsteps: []\n";
        var selector = new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-alpha",
                OperationId = "send-summary",
            },
        };
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "connector-workflow/send",
                Capability = new ExternalWorkflowCapabilityRef
                {
                    HostConnector = selector.HostConnector.Clone(),
                },
            }],
            [new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog,
                SourceId = "connector-catalog-fixture",
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "connector-catalog-digest",
            }]);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new ExternalToolInvocationSpec
            {
                CallSiteId = "connector-workflow/send",
                ToolName = "connector_call",
                Selector = selector,
            }]);

        act.Should().NotThrow();
    }

    [Fact]
    public void AdmissionPlanIntegrity_ShouldAcceptMatchingExplicitRequestProofAndGrant()
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var capability = ExplicitCapability(selector.NyxIdRequest);
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(capability)],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateOrThrow_ShouldRejectExplicitPlanWithoutExpectedWorkflowRevisionIdentity()
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(ExplicitCapability(selector.NyxIdRequest))],
            [ExplicitSource()],
            workflowId: "wf-alpha",
            revisionId: "rev-alpha");

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*binding identity is required*");
    }

    [Theory]
    [InlineData("request_user_service_id")]
    [InlineData("request_method")]
    [InlineData("request_path_template")]
    [InlineData("request_query_parameters")]
    [InlineData("request_header_parameters")]
    [InlineData("request_body_mode")]
    [InlineData("request_body_required")]
    [InlineData("request_response_mode")]
    [InlineData("admission_call_site_id")]
    public void AdmissionPlanIntegrity_ShouldRejectRehashedCanonicalBoundInvocationMutation(string mutation)
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelectorForMutation(mutation);
        var capability = ExplicitCapability(selector.NyxIdRequest);
        if (mutation is "request_body_mode" or "request_body_required")
        {
            capability.NyxIdUserRequest.ExecutionPolicy = ExplicitPolicy(
                NyxIdOperationRisk.Write,
                ExternalCapabilityExecutionMode.Interactive);
        }
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(capability)],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);
        var originalDigest = plan.AdmissionDigest;

        MutateCanonicalBoundInvocation(plan.InvocationAdmissions.Single(), mutation);
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        plan.AdmissionDigest.Should().NotBe(originalDigest);
        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Workflow capability invocation admissions do not match the bound definition.");
    }

    [Theory]
    [InlineData("service_slug_snapshot", "*explicit request proof digest is invalid*")]
    [InlineData("grant_call_site_id", "*explicit request grant scope is invalid*")]
    [InlineData("grant_request_contract_digest", "*explicit request grant scope is invalid*")]
    [InlineData("grant_owner_kind", "*explicit request grant digest is invalid*")]
    [InlineData("grant_owner_subject", "*explicit request grant digest is invalid*")]
    [InlineData("grant_risk", "*explicit request proof policy does not match its grant*")]
    [InlineData("grant_modes", "*explicit request proof policy does not match its grant*")]
    [InlineData("proof_contract_digest", "*explicit request proof digest is invalid*")]
    [InlineData("proof_grant_digest", "*explicit request grant digest is invalid*")]
    [InlineData("proof_policy_risk", "*explicit request proof policy does not match its grant*")]
    [InlineData("proof_policy_modes", "*explicit request proof policy does not match its grant*")]
    public void AdmissionPlanIntegrity_ShouldRejectRehashedValidProofOrGrantCorrespondenceMutation(
        string mutation,
        string expectedMessage)
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var capability = ExplicitCapability(selector.NyxIdRequest);
        if (mutation == "grant_risk")
        {
            capability.NyxIdUserRequest.ExecutionPolicy = ExplicitPolicy(
                NyxIdOperationRisk.ReadOnly,
                ExternalCapabilityExecutionMode.Interactive);
        }
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(capability)],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        MutateValidProofOrGrantCorrespondence(plan.InvocationAdmissions.Single(), mutation);
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage(expectedMessage);
    }

    [Theory]
    [InlineData("grant_missing", "*explicit request grant is required*")]
    [InlineData("grant_authority", "*explicit request grantor is invalid*")]
    [InlineData("proof_request_missing", "*explicit request proof request is required*")]
    [InlineData("proof_policy_missing", "*execution policy is invalid*")]
    [InlineData("proof_policy_approval", "*execution policy is invalid*")]
    [InlineData("proof_policy_owner", "*execution policy is invalid*")]
    public void AdmissionPlanIntegrity_ShouldRejectExplicitGrantOrProofPolicyIntegrityMutation(
        string mutation,
        string expectedMessage)
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(ExplicitCapability(selector.NyxIdRequest))],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        MutateGrantOrProofPolicyIntegrity(plan.InvocationAdmissions.Single(), mutation);
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage(expectedMessage);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_kind")]
    [InlineData("unusable_stamp")]
    public void ValidateOrThrow_ShouldFailClosedWhenExplicitRequestSourceEvidenceIsInvalid(string sourceCase)
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitAdmission(ExplicitCapability(selector.NyxIdRequest))],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        MutateExplicitSourceEvidence(plan, sourceCase);
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Workflow capability admission required source evidence is invalid.");
    }

    [Fact]
    public void Create_ShouldRejectExplicitProofWithoutRouteGrant()
    {
        var capability = ExplicitCapability(ExplicitSelector().NyxIdRequest);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = ExplicitCallSiteId,
                Capability = capability,
            }],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit request grant*");
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Put, NyxIdOperationRisk.ReadOnly)]
    [InlineData(NyxIdRequestMethod.Patch, NyxIdOperationRisk.ReadOnly)]
    [InlineData(NyxIdRequestMethod.Delete, NyxIdOperationRisk.Write)]
    public void Create_ShouldRejectExplicitGrantBelowMethodRiskFloor(
        NyxIdRequestMethod method,
        NyxIdOperationRisk grantedRisk)
    {
        var admission = ExplicitAdmissionFor(
            method,
            grantedRisk,
            ExternalCapabilityExecutionMode.Interactive);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [admission],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*risk*");
    }

    [Fact]
    public void Create_ShouldAcceptExplicitReadOnlyPostWhenRiskIsPartOfTheRequestContract()
    {
        var admission = ExplicitAdmissionFor(
            NyxIdRequestMethod.Post,
            NyxIdOperationRisk.ReadOnly,
            ExternalCapabilityExecutionMode.Interactive);

        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [admission],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        plan.InvocationAdmissions.Should().ContainSingle().Which
            .Capability.NyxIdUserRequest.ExecutionPolicy.Approval.Should()
            .Be(NyxIdOperationApproval.None);
    }

    [Fact]
    public void RequestContractDigest_ShouldPreserveVersionOneForUnspecifiedRisk()
    {
        var request = ExplicitSelector().NyxIdRequest;
        var expected = ExternalWorkflowCapabilityContractDigest.Compute(
            "nyxid-explicit-request-contract.v1",
            request.UserServiceId,
            ((int)request.Method).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.PathTemplate,
            string.Join("\n", NyxIdRequestSelectorContract.PathParameters(request).Order(StringComparer.Ordinal)),
            string.Join("\n", request.QueryParameters.Order(StringComparer.Ordinal)),
            string.Join("\n", request.HeaderParameters
                .Select(static value => value.ToLowerInvariant())
                .Order(StringComparer.Ordinal)),
            ((int)request.BodyMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.BodyRequired.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)request.ResponseMode).ToString(System.Globalization.CultureInfo.InvariantCulture));

        WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(request)
            .Should().Be(expected);
    }

    [Fact]
    public void Create_ShouldAcceptDurableExplicitReadOnlyPostGrant()
    {
        var admission = ExplicitAdmissionFor(
            NyxIdRequestMethod.Post,
            NyxIdOperationRisk.ReadOnly,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);

        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [admission],
            [ExplicitSource(), DurableCatalogSource()],
            DurableOwner(),
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        plan.InvocationAdmissions.Should().ContainSingle().Which
            .Capability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Should()
            .Contain(ExternalCapabilityExecutionMode.Durable);
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Post, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Put, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Patch, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Delete, NyxIdOperationRisk.Destructive)]
    public void Create_ShouldAcceptDurableExplicitWriteOrDestructiveGrant(
        NyxIdRequestMethod method,
        NyxIdOperationRisk grantedRisk)
    {
        var admission = ExplicitAdmissionFor(
            method,
            grantedRisk,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [admission],
            [ExplicitSource(), DurableCatalogSource()],
            DurableOwner(),
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateOrThrow_ShouldRequireCatalogAndOwnerForDurableExplicitReadGrant()
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [ExplicitAdmission(ExplicitCapability(selector.NyxIdRequest))],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source*");
    }

    [Fact]
    public void ValidateOrThrow_ShouldAcceptDurableExplicitReadGrantWithCatalogAndOwner()
    {
        const string yaml = "name: explicit-workflow\nsteps: []\n";
        var selector = ExplicitSelector();
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [ExplicitAdmission(ExplicitCapability(selector.NyxIdRequest))],
            [ExplicitSource(), DurableCatalogSource()],
            DurableOwner(),
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [ExplicitInvocation(selector)],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        act.Should().NotThrow();
    }

    [Fact]
    public void DistinctCapabilities_ShouldKeepExplicitCapabilitiesWithDifferentDerivedSlugs()
    {
        var first = ExplicitAdmission(ExplicitCapability(ExplicitSelector().NyxIdRequest));
        var secondCapability = ExplicitCapability(ExplicitSelector().NyxIdRequest);
        secondCapability.NyxIdUserRequest.ServiceSlugSnapshot = "svc-beta";
        secondCapability.NyxIdUserRequest.ContractDigest = ExplicitProofDigest(
            secondCapability.NyxIdUserRequest.Request,
            secondCapability.NyxIdUserRequest.ServiceSlugSnapshot);
        var second = ExplicitAdmission(secondCapability, "explicit-workflow/request-beta");
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: explicit-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [first, second],
            [ExplicitSource()],
            workflowId: ExplicitWorkflowId,
            revisionId: ExplicitRevisionId);

        WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(first.Capability).Should()
            .NotBe(WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(second.Capability));
        WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(plan).Should().HaveCount(2);
    }

    [Fact]
    public async Task AdmitAsync_ShouldNormalizeOrderedBundleByExactWorkflowName()
    {
        const string rootYaml = "name: root-workflow\nsteps: []\n";
        const string childYaml = "name: child-workflow\nsteps: []\n";
        var parser = new MappingParser(new Dictionary<string, WorkflowYamlParseResult>
        {
            [rootYaml.Trim()] = WorkflowYamlParseResult.Success("root-workflow"),
            [childYaml.Trim()] = WorkflowYamlParseResult.Success("child-workflow"),
        });
        var service = new WorkflowExternalCapabilityAdmissionService(
            parser,
            new StubReadinessPort(),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls(
                new ExternalWorkflowCapabilityAccessContext("scope-alpha", "caller-alpha"),
                [rootYaml, childYaml],
                "studio-member-binding",
                ExternalCapabilityExecutionMode.Interactive));

        plan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                rootYaml.Trim(),
                new Dictionary<string, string>
                {
                    ["child-workflow"] = childYaml.Trim(),
                }));
        parser.Calls.Should().Equal(rootYaml.Trim(), childYaml.Trim());
    }

    [Fact]
    public async Task AdmitAsync_ShouldSkipExternalReads_WhenDefinitionHasNoExternalCapabilities()
    {
        var parser = new StubParser(WorkflowYamlParseResult.Success(
            "wf-alpha",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
            }));
        var readiness = new StubReadinessPort();
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        plan.SchemaVersion.Should().Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        plan.InvocationAdmissions.Should().BeEmpty();
        plan.ExternalCapabilities.Should().BeEmpty();
        plan.SourceStamps.Should().BeEmpty();
        plan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                "name: wf-alpha\nsteps: []\n",
                new Dictionary<string, string>()));
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_ShouldSealResponseProjectionIntoTheCallSiteAdmission()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var dependencies = Dependencies(capability);
        dependencies.ExternalInvocations[0].ResponseProjection = new WorkflowToolResponseProjection
        {
            Fields =
            {
                new WorkflowToolResponseProjectionField
                {
                    OutputName = "instance_code",
                    Operations =
                    {
                        new WorkflowToolResponseProjectionOperation
                        {
                            JsonPointer = "/data/instance_code",
                        },
                    },
                },
            },
        };
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", dependencies)),
            new StubReadinessPort(Ready(capability)),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request(yaml));

        plan.InvocationAdmissions.Should().ContainSingle().Which.ResponseProjection
            .Should().Be(dependencies.ExternalInvocations[0].ResponseProjection);
        WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(
                plan,
                yaml,
                new Dictionary<string, string>(),
                ExternalCapabilityExecutionMode.Interactive,
                dependencies.ExternalInvocations)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AdmitAsync_CodeExecute_ShouldCommitExactRouteProof()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var selector = new ExternalWorkflowCapabilitySelector
        {
            CodeExecution = new CodeExecutionSelector(),
        };
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = selector,
        });
        var proof = new CodeExecutionCapabilityRef
        {
            UserServiceId = "us-code-alpha",
            ServiceSlugSnapshot = "chrono-sandbox",
            CatalogServiceId = "catalog-chrono-sandbox",
        };
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        var capability = new ExternalWorkflowCapabilityRef { CodeExecution = proof };
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector,
            SelectedCapability = capability,
            Sources =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                    SourceId = "nyxid-user-services:caller:caller-alpha",
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "code-route-inventory-digest",
                },
            },
        };
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request(yaml));

        plan.SchemaVersion.Should().Be("external-capability-admission.v6");
        plan.InvocationAdmissions.Should().ContainSingle().Which
            .Capability.CodeExecution.UserServiceId.Should().Be("us-code-alpha");
        plan.SourceStamps.Should().ContainSingle().Which.SourceKind.Should()
            .Be(ExternalCapabilitySourceKind.NyxIdUserServices);
    }

    [Fact]
    public async Task AdmitAsync_CodeExecute_InspectsAllThenConvergesOnceAndFreshlyVerifiesAll()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var selector = new ExternalWorkflowCapabilitySelector
        {
            CodeExecution = new CodeExecutionSelector(),
        };
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code-a",
            ToolName = "code_execute",
            Selector = selector.Clone(),
        });
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code-b",
            ToolName = "code_execute",
            Selector = selector.Clone(),
        });
        var proof = new CodeExecutionCapabilityRef
        {
            UserServiceId = "us-code-alpha",
            ServiceSlugSnapshot = "chrono-sandbox",
            CatalogServiceId = "catalog-chrono-sandbox",
        };
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector,
            SelectedCapability = new ExternalWorkflowCapabilityRef { CodeExecution = proof },
            Sources =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                    SourceId = "nyxid-user-services:caller:caller-alpha",
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "code-route-inventory-digest",
                },
            },
        };
        var drift = ConvergeableDrift(selector);
        var readinessPort = new SequenceReadinessPort([
            drift,
            drift.Clone(),
            readiness,
            readiness.Clone(),
        ]);
        var preparer = new RecordingAdmissionPreparer();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            readinessPort,
            new FixedTimeProvider(),
            [preparer]);

        var plan = await service.AdmitAsync(Request(yaml));

        preparer.Calls.Should().Be(1);
        preparer.LastAccess!.CallerId.Should().Be("caller-alpha");
        preparer.LastExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        readinessPort.Calls.Should().Be(4);
        plan.InvocationAdmissions.Should().HaveCount(2);
    }

    [Fact]
    public async Task AdmitAsync_MixedWorkflow_DoesNotPrepareCodeBeforeOtherCapabilityPreflightPasses()
    {
        const string yaml = "name: mixed-workflow\nsteps: []\n";
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "mixed-workflow/run-code",
            ToolName = "code_execute",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                CodeExecution = new CodeExecutionSelector(),
            },
        });
        var connectorSelector = new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-alpha",
                OperationId = "send-message",
            },
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "mixed-workflow/send-message",
            ToolName = "connector_call",
            Selector = connectorSelector,
        });
        var blocked = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
            SelectedSelector = connectorSelector,
        };
        blocked.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = blocked.Status,
            Code = "CONNECTOR_NOT_READY",
            SafeMessage = "The connector is not ready.",
        });
        var readinessPort = new SequenceReadinessPort([
            ConvergeableDrift(dependencies.ExternalInvocations[0].Selector),
            blocked,
        ]);
        var preparer = new RecordingAdmissionPreparer();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("mixed-workflow", dependencies)),
            readinessPort,
            new FixedTimeProvider(),
            [preparer]);

        var act = () => service.AdmitAsync(Request(yaml));

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CONNECTOR_NOT_READY");
        readinessPort.Calls.Should().Be(2);
        preparer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_DistinctSelectorsOfSameKind_ConvergesEachExactIdentity()
    {
        const string yaml = "name: service-workflow\nsteps: []\n";
        var capabilityAlpha = NyxIdCapability();
        var capabilityBeta = NyxIdCapability().Clone();
        capabilityBeta.NyxIdUserService.UserServiceId = "us-home-beta";
        capabilityBeta.NyxIdUserService.EndpointId = "get-state-beta";
        capabilityBeta.NyxIdUserService.ContractDigest = "operation-digest-beta";
        var selectorAlpha = Selector(capabilityAlpha);
        var selectorBeta = Selector(capabilityBeta);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "service-workflow/read-alpha",
            ToolName = "nyxid_proxy",
            Selector = selectorAlpha,
        });
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "service-workflow/read-beta",
            ToolName = "nyxid_proxy",
            Selector = selectorBeta,
        });
        var readinessPort = new SequenceReadinessPort([
            ConvergeableDrift(selectorAlpha),
            ConvergeableDrift(selectorBeta),
            Ready(capabilityAlpha),
            Ready(capabilityBeta),
        ]);
        var preparer = new RecordingAdmissionPreparer(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("service-workflow", dependencies)),
            readinessPort,
            new FixedTimeProvider(),
            [preparer]);

        var plan = await service.AdmitAsync(Request(yaml));

        preparer.Calls.Should().Be(2);
        preparer.PreparedSelectorKeys.Should().BeEquivalentTo([
            WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(selectorAlpha),
            WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(selectorBeta),
        ]);
        readinessPort.Calls.Should().Be(4);
        plan.InvocationAdmissions.Should().HaveCount(2);
    }

    [Fact]
    public async Task AdmitAsync_ShouldClassifyUnresolvedNyxIdInvocationAsOperationSelectionRequired()
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = CallSiteId,
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector(),
        });
        var readiness = new StubReadinessPort();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", dependencies)),
            readiness,
            new FixedTimeProvider());

        Func<Task> act = async () =>
            await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.OperationSelectionRequired);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_SELECTION_REQUIRED");
        exception.Which.Readiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.SelectOperation);
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_ShouldPreserveTypedAuthoringFailureFromParser()
    {
        var parserReadiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-alpha",
                    EndpointId = "get-resource",
                },
            },
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.ContractDrift,
                    Code = "NYXID_OPERATION_ARGUMENT_INVALID",
                    SafeMessage = "The NyxID operation runtime arguments are invalid.",
                },
            },
            Remediations =
            {
                new ExternalCapabilityRemediation
                {
                    ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
                    Label = "Update and rebind workflow",
                },
            },
        };
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Invalid("internal parser detail", parserReadiness)),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_ARGUMENT_INVALID");
        exception.Which.Message.Should().NotContain("internal parser detail");
    }

    [Fact]
    public async Task AdmitAsync_ShouldSealExactReadyCapabilityAndSourceEvidence()
    {
        var capability = NyxIdCapability();
        var dependencies = Dependencies(capability);
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", dependencies));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        plan.ExternalCapabilities.Should().BeEmpty();
        plan.InvocationAdmissions.Should().ContainSingle();
        plan.InvocationAdmissions[0].CallSiteId.Should().Be(CallSiteId);
        plan.InvocationAdmissions[0].Capability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        plan.SourceStamps.Select(static source => source.SourceKind).Should().BeEquivalentTo([
            ExternalCapabilitySourceKind.NyxIdMcpConfig,
        ]);
        plan.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectBeforeMutation_WhenAnyCapabilityIsNotReady()
    {
        var readinessResult = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
        };
        readinessResult.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readinessResult.Status,
            Code = "CREDENTIAL_CONNECTION_REQUIRED",
            SafeMessage = "Connect the selected service credential.",
        });
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(NyxIdCapability()))),
            new StubReadinessPort(readinessResult),
            new FixedTimeProvider());

        Func<Task> act = async () =>
            await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.CredentialConnectionRequired);
        exception.Which.Message.Should().NotContain("runtime-caller-credential");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectReadyProofForDifferentExecutionMode()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(capability)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_EXECUTION_MODE_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectReadyProofForDifferentSelector()
    {
        var capability = NyxIdCapability();
        var different = NyxIdCapability();
        different.NyxIdUserService.UserServiceId = "us-home-beta";
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(
                different,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_SELECTOR_PROOF_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectDurableReadyProofWithoutCatalogSource()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(capability, ExternalCapabilityExecutionMode.Durable)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectReadyProofWithoutNyxIdMcpCatalogSource()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(capability);
        var catalog = readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.NyxIdMcpConfig);
        readiness.Sources.Remove(catalog);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectDurableCatalogSourceForDifferentCaller()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true);
        readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog).SourceId =
            CatalogSourceId("caller-beta");
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_SOURCE_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldSealVerifiedDurableAuthorizationOwner()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(
                capability,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true)),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        plan.DurableAuthorizationOwner.Should().BeEquivalentTo(
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectExpiredReadyProofBeforeSealingPlan()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(capability);
        foreach (var source in readiness.Sources)
        {
            source.ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(-2));
            source.FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(-1));
        }
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("ADMISSION_SOURCE_STALE");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldVerifyPlanWithoutRepeatingExternalRead()
    {
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var initial = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                initial,
                "name: wf-alpha\nsteps: []\n",
                new Dictionary<string, string>(),
                "scope-workflow",
                ExternalCapabilityExecutionMode.Interactive));

        verified.Should().BeEquivalentTo(initial);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersistedAsync_StaleCodePlan_ShouldValidateThenConvergeAndSealFreshSource()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var selector = CodeExecutionSelector();
        var capability = CodeExecutionCapability();
        var dependencies = CodeExecutionDependencies(selector);
        var staleSource = CodeExecutionSource(
            "stale-route-inventory-digest",
            FixedTimeProvider.Now.AddMinutes(-10),
            FixedTimeProvider.Now.AddMinutes(-1));
        var persistedPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = CodeExecutionCallSiteId,
                Capability = capability,
            }],
            [staleSource]);
        var freshReadiness = CodeExecutionReady(
            selector,
            capability,
            "fresh-route-inventory-digest");
        var readiness = new SequenceReadinessPort([
            ConvergeableDrift(selector),
            freshReadiness,
        ]);
        var preparer = new RecordingAdmissionPreparer();
        var parser = new MappingParser(new Dictionary<string, WorkflowYamlParseResult>
        {
            [yaml] = WorkflowYamlParseResult.Success("code-workflow", dependencies),
        });
        var service = new WorkflowExternalCapabilityAdmissionService(
            parser,
            readiness,
            new FixedTimeProvider(),
            [preparer]);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("runtime-caller-credential"));

        var tamperedPlan = persistedPlan.Clone();
        tamperedPlan.DefinitionDigest = "tampered-definition-digest";
        Func<Task> invalid = async () => await service.RefreshPersistedAsync(
            new RefreshPersistedWorkflowCapabilityAdmissionRequest(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    tamperedPlan,
                    yaml,
                    new Dictionary<string, string>(),
                    "scope-workflow",
                    ExternalCapabilityExecutionMode.Interactive),
                access));

        await invalid.Should().ThrowAsync<InvalidOperationException>();
        readiness.Calls.Should().Be(0);
        preparer.Calls.Should().Be(0);

        var refreshed = await service.RefreshPersistedAsync(
            new RefreshPersistedWorkflowCapabilityAdmissionRequest(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    persistedPlan,
                    yaml,
                    new Dictionary<string, string>(),
                    "scope-workflow",
                    ExternalCapabilityExecutionMode.Interactive),
                access));

        parser.Calls.Should().Equal(yaml, yaml, yaml);
        readiness.Calls.Should().Be(2);
        preparer.Calls.Should().Be(1);
        preparer.LastAccess.Should().BeSameAs(access);
        refreshed.SourceStamps.Should().ContainSingle().Which.ContentDigest.Should()
            .Be("fresh-route-inventory-digest");
        refreshed.SourceStamps.Single().FreshUntil.ToDateTimeOffset().Should()
            .BeAfter(FixedTimeProvider.Now);
        refreshed.InvocationAdmissions.Should().ContainSingle().Which.Capability.Should()
            .BeEquivalentTo(capability);
        persistedPlan.SourceStamps.Should().ContainSingle().Which.ContentDigest.Should()
            .Be("stale-route-inventory-digest");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_StaleCodePlan_ShouldRejectWithoutReadsOrWrites()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var selector = CodeExecutionSelector();
        var capability = CodeExecutionCapability();
        var persistedPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = CodeExecutionCallSiteId,
                Capability = capability,
            }],
            [CodeExecutionSource(
                "stale-route-inventory-digest",
                FixedTimeProvider.Now.AddMinutes(-10),
                FixedTimeProvider.Now.AddMinutes(-1))]);
        var readiness = new StubReadinessPort();
        var preparer = new RecordingAdmissionPreparer();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success(
                "code-workflow",
                CodeExecutionDependencies(selector))),
            readiness,
            new FixedTimeProvider(),
            [preparer]);

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                persistedPlan,
                yaml,
                new Dictionary<string, string>(),
                "scope-workflow",
                ExternalCapabilityExecutionMode.Interactive));

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("ADMISSION_SOURCE_STALE");
        readiness.Calls.Should().Be(0);
        preparer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ExistingV4NyxIdPlan_ShouldRemainValid()
    {
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var existing = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));
        existing.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion;
        existing.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(existing);

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existing,
                "name: wf-alpha\nsteps: []\n",
                new Dictionary<string, string>(),
                "scope-workflow",
                ExternalCapabilityExecutionMode.Interactive));

        verified.Should().BeEquivalentTo(existing);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ExistingV4CodeExecutePlan_ShouldNotRequireRebind()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                CodeExecution = new CodeExecutionSelector(),
            },
        });
        var existing = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        existing.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion;
        existing.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(existing);
        var readiness = new StubReadinessPort();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            readiness,
            new FixedTimeProvider());

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existing,
                yaml,
                new Dictionary<string, string>(),
                "scope-workflow",
                ExternalCapabilityExecutionMode.Interactive));

        verified.Should().BeEquivalentTo(existing);
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldReturnTypedRebindReadiness_ForLegacyPlan()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var legacyPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha")),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                legacyPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Interactive));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
        exception.Which.Readiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.RebindWorkflow);
    }

    [Theory]
    [InlineData("external-capability-admission.v2")]
    [InlineData("external-capability-admission.v3")]
    [InlineData("external-capability-admission.v5")]
    public async Task RevalidatePersistedAsync_ShouldClassifyRebindSchemaBeforeParsingOldAuthoring(
        string schemaVersion)
    {
        var legacyPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = schemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var parser = new StubParser(WorkflowYamlParseResult.Invalid("legacy proof fields are invalid"));
        var service = new WorkflowExternalCapabilityAdmissionService(
            parser,
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                legacyPlan,
                "name: legacy\nsteps: []\n",
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Interactive));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectPlanForDifferentExpectedExecutionMode()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha")),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "scheduled_workflow_replay",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution mode*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldAcceptDurablePlanWithoutTransientCaller()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var initial = await service.AdmitAsync(Request(
            yaml,
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                initial,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        verified.Should().BeEquivalentTo(initial);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectDurableNyxIdPlanWithoutCatalogSource()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(capability).Sources,
            DurableOwner());
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectPlanForDifferentCatalogOwner()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var readiness = Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true);
        readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog).SourceId =
            CatalogSourceId("caller-beta");
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            readiness.Sources,
            DurableOwner());
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source does not match the persisted owner*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectDurableNyxIdPlanWithoutOwner()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(capability, owner: null);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization owner is invalid*");
    }

    [Theory]
    [InlineData("aevatar", ExternalCapabilityAuthorizationOwnerKind.Personal, "caller-alpha")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Organization, "caller-alpha")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, "")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, "   ")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, " caller-alpha")]
    public async Task RevalidatePersistedAsync_ShouldRejectMalformedDurableAuthorizationOwner(
        string authority,
        ExternalCapabilityAuthorizationOwnerKind ownerKind,
        string ownerSubject)
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(
            capability,
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = authority,
                OwnerKind = ownerKind,
                OwnerSubject = ownerSubject,
            });
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization owner is invalid*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectChangedOwnerAfterAdmissionDigestIsRecomputed()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(capability, DurableOwner());
        var originalAdmissionDigest = existingPlan.AdmissionDigest;
        existingPlan.DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
        {
            Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = "caller-beta",
        };
        existingPlan.AdmissionDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(existingPlan);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        existingPlan.AdmissionDigest.Should().NotBe(originalAdmissionDigest);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source does not match the persisted owner*");
    }

    private static WorkflowExternalCapabilityAdmissionRequest Request(
        string yaml,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive) =>
        new(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "caller-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "runtime-caller-credential")),
            yaml,
            new Dictionary<string, string>(),
            "scope-workflow",
            executionMode);

    private const string CodeExecutionCallSiteId = "code-workflow/run-code";

    private static ExternalWorkflowCapabilitySelector CodeExecutionSelector() => new()
    {
        CodeExecution = new CodeExecutionSelector(),
    };

    private static ExternalWorkflowCapabilityRef CodeExecutionCapability()
    {
        var proof = new CodeExecutionCapabilityRef
        {
            UserServiceId = "us-code-alpha",
            ServiceSlugSnapshot = "chrono-sandbox",
            CatalogServiceId = "catalog-chrono-sandbox",
        };
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        return new ExternalWorkflowCapabilityRef { CodeExecution = proof };
    }

    private static WorkflowAuthorizationDependencies CodeExecutionDependencies(
        ExternalWorkflowCapabilitySelector selector)
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = CodeExecutionCallSiteId,
            ToolName = "code_execute",
            Selector = selector.Clone(),
        });
        return dependencies;
    }

    private static ExternalCapabilitySourceStamp CodeExecutionSource(
        string contentDigest,
        DateTimeOffset observedAt,
        DateTimeOffset freshUntil) =>
        new()
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = "nyxid-user-services:caller:caller-alpha",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(freshUntil),
            ContentDigest = contentDigest,
        };

    private static ExternalCapabilityReadiness CodeExecutionReady(
        ExternalWorkflowCapabilitySelector selector,
        ExternalWorkflowCapabilityRef capability,
        string contentDigest) =>
        new()
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability.Clone(),
            Sources =
            {
                CodeExecutionSource(
                    contentDigest,
                    FixedTimeProvider.Now,
                    FixedTimeProvider.Now.AddMinutes(5)),
            },
        };

    private static ExternalCapabilityAuthorizationOwner DurableOwner() => new()
    {
        Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
        OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
        OwnerSubject = "caller-alpha",
    };

    private static WorkflowCapabilityAdmissionPlan DurablePlan(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityAuthorizationOwner? owner) =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(
                capability,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true).Sources,
            owner);

    private const string CallSiteId = "wf-alpha/read-state";

    private static WorkflowAuthorizationDependencies Dependencies(
        ExternalWorkflowCapabilityRef capability)
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = CallSiteId,
            ToolName = "nyxid_proxy",
            Selector = Selector(capability),
        });
        return dependencies;
    }

    private static ExternalWorkflowCapabilitySelector Selector(
        ExternalWorkflowCapabilityRef capability) =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = capability.NyxIdUserService.UserServiceId,
                EndpointId = capability.NyxIdUserService.EndpointId,
            },
        };

    private static WorkflowCapabilityInvocationAdmission[] Admissions(
        ExternalWorkflowCapabilityRef capability) =>
        [new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = CallSiteId,
            Capability = capability,
        }];

    private static ExternalCapabilityReadiness Ready(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        bool includeDurableCatalog = false)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = Selector(capability),
            SelectedCapability = capability,
            Sources =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
                    SourceId = "nyxid-mcp-config:caller:nyx-user-alpha",
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "mcp-config-digest",
                },
            },
        };
        if (includeDurableCatalog)
        {
            readiness.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                SourceId = CatalogSourceId("caller-alpha"),
                SourceVersion = 17,
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "catalog-digest",
            });
        }
        return readiness;
    }

    private static ExternalCapabilityReadiness ConvergeableDrift(
        ExternalWorkflowCapabilitySelector selector)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = selector.Clone(),
            Sources =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                    SourceId = "nyxid-user-services:caller:caller-alpha",
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "route-inventory-digest",
                },
            },
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = RecordingAdmissionPreparer.ConvergeableBlockerCode,
            SafeMessage = "The selected route requires convergence.",
        });
        return readiness;
    }

    private static string CatalogSourceId(string callerId) =>
        NyxIdAuthorizationCatalogActorIds.Build(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = callerId,
        });

    private static ExternalWorkflowCapabilityRef NyxIdCapability() =>
        new()
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                EndpointId = "get-state",
                HttpMethod = "GET",
                PathTemplate = "/states/{entity_id}",
                ContractDigest = "operation-digest",
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.ReadOnly,
                    Approval = NyxIdOperationApproval.None,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes =
                    {
                        ExternalCapabilityExecutionMode.Interactive,
                        ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };

    private const string ExplicitCallSiteId = "explicit-workflow/request-alpha";
    private const string ExplicitWorkflowId = "wf-alpha";
    private const string ExplicitRevisionId = "rev-alpha";

    private static ExternalWorkflowCapabilitySelector ExplicitSelector()
    {
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        request.QueryParameters.Add("page_size");
        request.HeaderParameters.Add("If-Match");
        return new ExternalWorkflowCapabilitySelector { NyxIdRequest = request };
    }

    private static ExternalWorkflowCapabilitySelector ExplicitSelectorForMutation(string mutation)
    {
        var selector = ExplicitSelector();
        if (mutation is "request_body_mode" or "request_body_required")
        {
            selector.NyxIdRequest.Method = NyxIdRequestMethod.Post;
            selector.NyxIdRequest.BodyMode = NyxIdRequestBodyMode.Json;
        }
        return selector;
    }

    private static ExternalWorkflowCapabilityRef ExplicitCapability(NyxIdRequestSelector request)
    {
        const string slug = "svc-alpha";
        var requestDigest = ExplicitRequestContractDigest(request);
        return new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = request.Clone(),
                ServiceSlugSnapshot = slug,
                ContractDigest = ExternalWorkflowCapabilityContractDigest.Compute(
                    "nyxid-explicit-request-proof.v1",
                    requestDigest,
                    slug),
                ExecutionPolicy = ExplicitPolicy(
                    NyxIdOperationRisk.ReadOnly,
                    ExternalCapabilityExecutionMode.Interactive,
                    ExternalCapabilityExecutionMode.Durable),
            },
        };
    }

    private static WorkflowCapabilityInvocationAdmission ExplicitAdmission(
        ExternalWorkflowCapabilityRef capability,
        string callSiteId = ExplicitCallSiteId)
    {
        var policy = capability.NyxIdUserRequest.ExecutionPolicy;
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = ExplicitWorkflowId,
            RevisionId = ExplicitRevisionId,
            CallSiteId = callSiteId,
            RequestContractDigest = ExplicitRequestContractDigest(
                capability.NyxIdUserRequest.Request),
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "binder-alpha",
            Risk = policy.Risk,
        };
        grant.AllowedExecutionModes.Add(policy.AllowedExecutionModes);
        capability.NyxIdUserRequest.ExplicitRequestGrantDigest = ExplicitGrantDigest(grant);
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = callSiteId,
            Capability = capability,
            NyxIdExplicitRequestGrant = grant,
        };
    }

    private static WorkflowCapabilityInvocationAdmission ExplicitAdmissionFor(
        NyxIdRequestMethod method,
        NyxIdOperationRisk risk,
        params ExternalCapabilityExecutionMode[] modes)
    {
        var request = ExplicitSelector().NyxIdRequest;
        request.Method = method;
        if (method == NyxIdRequestMethod.Post && risk == NyxIdOperationRisk.ReadOnly)
            request.Risk = risk;
        var capability = ExplicitCapability(request);
        capability.NyxIdUserRequest.ExecutionPolicy = ExplicitPolicy(risk, modes);
        return ExplicitAdmission(capability);
    }

    private static ExternalToolInvocationSpec ExplicitInvocation(
        ExternalWorkflowCapabilitySelector selector) =>
        new()
        {
            CallSiteId = ExplicitCallSiteId,
            ToolName = "nyxid_proxy",
            Selector = selector,
        };

    private static ExternalCapabilitySourceStamp ExplicitSource() =>
        new()
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = "nyxid-user-services:caller:binder-alpha",
            ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
            FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
            ContentDigest = "user-services-digest",
        };

    private static ExternalCapabilitySourceStamp DurableCatalogSource() =>
        new()
        {
            SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
            SourceId = CatalogSourceId("caller-alpha"),
            SourceVersion = 17,
            ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
            FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
            ContentDigest = "catalog-digest",
        };

    private static NyxIdOperationExecutionPolicy ExplicitPolicy(
        NyxIdOperationRisk risk,
        params ExternalCapabilityExecutionMode[] modes)
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = risk,
            Approval = risk == NyxIdOperationRisk.ReadOnly
                ? NyxIdOperationApproval.None
                : NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(modes);
        return policy;
    }

    private static string ExplicitRequestContractDigest(NyxIdRequestSelector request) =>
        WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(request);

    private static string ExplicitProofDigest(NyxIdRequestSelector request, string slug) =>
        ExternalWorkflowCapabilityContractDigest.Compute(
            "nyxid-explicit-request-proof.v1",
            ExplicitRequestContractDigest(request),
            slug);

    private static string ExplicitGrantDigest(NyxIdExplicitRequestGrant grant) =>
        WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);

    private static void MutateCanonicalBoundInvocation(
        WorkflowCapabilityInvocationAdmission admission,
        string mutation)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var request = proof.Request;
        var grant = admission.NyxIdExplicitRequestGrant;
        switch (mutation)
        {
            case "request_user_service_id":
                request.UserServiceId = "usvc-beta";
                break;
            case "request_method":
                request.Method = NyxIdRequestMethod.Head;
                break;
            case "request_path_template":
                request.PathTemplate = "/api/other/{resource_id}";
                break;
            case "request_query_parameters":
                request.QueryParameters.Insert(0, "filter");
                break;
            case "request_header_parameters":
                request.HeaderParameters.Add("If-None-Match");
                break;
            case "request_body_mode":
                request.BodyMode = NyxIdRequestBodyMode.None;
                break;
            case "request_body_required":
                request.BodyRequired = true;
                break;
            case "request_response_mode":
                request.ResponseMode = NyxIdRequestResponseMode.FileArtifact;
                break;
            case "service_slug_snapshot":
                proof.ServiceSlugSnapshot = "svc-beta";
                break;
            case "admission_call_site_id":
                admission.CallSiteId = "explicit-workflow/request-beta";
                grant.CallSiteId = admission.CallSiteId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        RehashExplicitAdmissionCorrespondence(admission);
    }

    private static void MutateValidProofOrGrantCorrespondence(
        WorkflowCapabilityInvocationAdmission admission,
        string mutation)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var grant = admission.NyxIdExplicitRequestGrant;
        switch (mutation)
        {
            case "service_slug_snapshot":
                proof.ServiceSlugSnapshot = "svc-beta";
                break;
            case "grant_call_site_id":
                grant.CallSiteId = "explicit-workflow/request-beta";
                break;
            case "grant_request_contract_digest":
                grant.RequestContractDigest = "forged-request-digest";
                break;
            case "grant_authority":
                grant.GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.Unspecified;
                break;
            case "grant_owner_kind":
                grant.GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Organization;
                break;
            case "grant_owner_subject":
                grant.GrantorOwnerSubject = "binder-beta";
                break;
            case "grant_risk":
                grant.Risk = NyxIdOperationRisk.Write;
                break;
            case "grant_modes":
                grant.AllowedExecutionModes.Clear();
                grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
                break;
            case "proof_contract_digest":
                proof.ContractDigest = "forged-proof-digest";
                break;
            case "proof_grant_digest":
                proof.ExplicitRequestGrantDigest = "forged-grant-digest";
                break;
            case "proof_policy_risk":
                proof.ExecutionPolicy.Risk = NyxIdOperationRisk.Write;
                proof.ExecutionPolicy.Approval = NyxIdOperationApproval.Required;
                proof.ExecutionPolicy.AllowedExecutionModes.Clear();
                proof.ExecutionPolicy.AllowedExecutionModes.Add(
                    ExternalCapabilityExecutionMode.Interactive);
                break;
            case "proof_policy_modes":
                proof.ExecutionPolicy.AllowedExecutionModes.Clear();
                proof.ExecutionPolicy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static void MutateGrantOrProofPolicyIntegrity(
        WorkflowCapabilityInvocationAdmission admission,
        string mutation)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        switch (mutation)
        {
            case "grant_missing":
                admission.NyxIdExplicitRequestGrant = null;
                break;
            case "grant_authority":
                admission.NyxIdExplicitRequestGrant.GrantorAuthority =
                    NyxIdExplicitRequestGrantorAuthority.Unspecified;
                break;
            case "proof_request_missing":
                proof.Request = null;
                break;
            case "proof_policy_missing":
                proof.ExecutionPolicy = null;
                break;
            case "proof_policy_approval":
                proof.ExecutionPolicy.Approval = NyxIdOperationApproval.Required;
                break;
            case "proof_policy_owner":
                proof.ExecutionPolicy.EnforcementOwner = NyxIdOperationEnforcementOwner.NyxId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static void RehashExplicitAdmissionCorrespondence(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var grant = admission.NyxIdExplicitRequestGrant;
        grant.RequestContractDigest = ExplicitRequestContractDigest(proof.Request);
        proof.ContractDigest = ExplicitProofDigest(proof.Request, proof.ServiceSlugSnapshot);
        proof.ExplicitRequestGrantDigest = ExplicitGrantDigest(grant);
    }

    private static void MutateExplicitSourceEvidence(
        WorkflowCapabilityAdmissionPlan plan,
        string sourceCase)
    {
        switch (sourceCase)
        {
            case "missing":
                plan.SourceStamps.Clear();
                break;
            case "wrong_kind":
                plan.SourceStamps.Single().SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog;
                break;
            case "unusable_stamp":
                plan.SourceStamps.Single().ContentDigest = string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sourceCase));
        }
    }

    private sealed class StubParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) => Task.FromResult(result);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            Task.FromResult(ToBundleResult(result, inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty));
    }

    private sealed class MappingParser(
        IReadOnlyDictionary<string, WorkflowYamlParseResult> results) : IWorkflowDefinitionParser
    {
        public List<string> Calls { get; } = [];

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            Calls.Add(workflowYaml);
            return Task.FromResult(results.TryGetValue(workflowYaml, out var result)
                ? result
                : throw new InvalidOperationException($"Unexpected workflow YAML: {workflowYaml}"));
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var document = inlineWorkflowDocuments[0];
            var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
            return ToBundleResult(parseResult, document.Yaml);
        }
    }

    private static WorkflowInlineYamlBundleParseResult ToBundleResult(
        WorkflowYamlParseResult parseResult,
        string workflowYaml) =>
        parseResult.Succeeded
            ? WorkflowInlineYamlBundleParseResult.Success(
                parseResult.WorkflowName,
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [parseResult.WorkflowName] = workflowYaml,
                })
            : WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

    private sealed class StubReadinessPort(ExternalCapabilityReadiness? result = null)
        : IExternalWorkflowCapabilityReadinessPort
    {
        public int Calls { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result?.Clone() ?? throw new InvalidOperationException("Unexpected readiness read."));
        }
    }

    private sealed class SequenceReadinessPort(
        IReadOnlyList<ExternalCapabilityReadiness> results)
        : IExternalWorkflowCapabilityReadinessPort
    {
        public int Calls { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Calls >= results.Count)
                throw new InvalidOperationException("Unexpected readiness read.");

            return Task.FromResult(results[Calls++].Clone());
        }
    }

    private sealed class RecordingAdmissionPreparer(
        ExternalWorkflowCapabilitySelector.SelectorOneofCase selectorKind =
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution)
        : IExternalWorkflowCapabilityAdmissionPreparer
    {
        public const string ConvergeableBlockerCode = "TEST_ROUTE_CONVERGENCE_REQUIRED";

        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind => selectorKind;

        public int Calls { get; private set; }
        public ExternalWorkflowCapabilityAccessContext? LastAccess { get; private set; }
        public ExternalCapabilityExecutionMode LastExecutionMode { get; private set; }
        public List<string> PreparedSelectorKeys { get; } = [];

        public bool CanConverge(ExternalCapabilityReadiness readiness) =>
            readiness.Status == ExternalCapabilityReadinessStatus.ContractDrift &&
            readiness.Blockers.Any(static blocker =>
                string.Equals(blocker.Code, ConvergeableBlockerCode, StringComparison.Ordinal));

        public Task PrepareAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selector.SelectorCase.Should().Be(SelectorKind);
            Calls++;
            PreparedSelectorKeys.Add(WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(selector));
            LastAccess = access;
            LastExecutionMode = executionMode;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public static DateTimeOffset Now { get; } =
            new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
