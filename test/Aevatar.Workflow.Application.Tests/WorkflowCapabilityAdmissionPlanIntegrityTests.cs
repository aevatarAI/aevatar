using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowCapabilityAdmissionPlanIntegrityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");

    [Theory]
    [InlineData("schema", WorkflowCapabilityAdmissionCompatibilityFailure.SchemaMismatch)]
    [InlineData("mode", WorkflowCapabilityAdmissionCompatibilityFailure.ExecutionModeMismatch)]
    [InlineData("definition", WorkflowCapabilityAdmissionCompatibilityFailure.DefinitionDigestMismatch)]
    [InlineData("call-site-count", WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch)]
    [InlineData("call-site-order", WorkflowCapabilityAdmissionCompatibilityFailure.InvocationOrderingInvalid)]
    [InlineData("selector", WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch)]
    [InlineData("proof", WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionProofInvalid)]
    [InlineData("owner", WorkflowCapabilityAdmissionCompatibilityFailure.DurableOwnerInvalid)]
    [InlineData("source", WorkflowCapabilityAdmissionCompatibilityFailure.RequiredSourceMissing)]
    [InlineData("source-order", WorkflowCapabilityAdmissionCompatibilityFailure.InvocationOrderingInvalid)]
    [InlineData("digest", WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionDigestMismatch)]
    public void CheckCompatibility_WithMutatedPlan_ShouldReturnTypedFailure(
        string mutation,
        WorkflowCapabilityAdmissionCompatibilityFailure expected)
    {
        var fixture = HostFixture();
        var workflowYaml = fixture.WorkflowYaml;

        switch (mutation)
        {
            case "schema":
                fixture.Plan.SchemaVersion = "external-capability-admission.v5";
                break;
            case "mode":
                fixture.Plan.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
                break;
            case "definition":
                workflowYaml += "# changed\n";
                break;
            case "call-site-count":
                fixture.Plan.InvocationAdmissions.RemoveAt(1);
                Rehash(fixture.Plan);
                break;
            case "call-site-order":
                var reversed = fixture.Plan.InvocationAdmissions.Reverse().ToArray();
                fixture.Plan.InvocationAdmissions.Clear();
                fixture.Plan.InvocationAdmissions.Add(reversed);
                Rehash(fixture.Plan);
                break;
            case "selector":
                fixture.Plan.InvocationAdmissions[0].Capability.HostConnector.OperationId = "other-operation";
                Rehash(fixture.Plan);
                break;
            case "proof":
                fixture.Plan.InvocationAdmissions[0].Capability = null;
                Rehash(fixture.Plan);
                break;
            case "owner":
                fixture.Plan.DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
                {
                    Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                    OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                    OwnerSubject = "caller-alpha",
                };
                Rehash(fixture.Plan);
                break;
            case "source":
                fixture.Plan.SourceStamps.Clear();
                Rehash(fixture.Plan);
                break;
            case "source-order":
                var reversedSources = fixture.Plan.SourceStamps.Reverse().ToArray();
                fixture.Plan.SourceStamps.Clear();
                fixture.Plan.SourceStamps.Add(reversedSources);
                Rehash(fixture.Plan);
                break;
            case "digest":
                fixture.Plan.AdmissionDigest = "digest-other";
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation: {mutation}");
        }

        var result = WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(
            fixture.Plan,
            workflowYaml,
            fixture.InlineWorkflowYamls,
            fixture.ExecutionMode,
            fixture.ExpectedInvocations,
            fixture.WorkflowId,
            fixture.RevisionId);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(expected);
    }

    [Fact]
    public void CheckCompatibility_WithLegacySchema_ShouldRequireRebind()
    {
        var fixture = HostFixture();
        fixture.Plan.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.OpenApiSchemaVersion;

        var result = Check(fixture);

        result.Failure.Should().Be(
            WorkflowCapabilityAdmissionCompatibilityFailure.RebindRequiredSchema);
    }

    [Fact]
    public void CheckCompatibility_WithChangedExplicitRequestGrantDigest_ShouldRejectProof()
    {
        var fixture = ExplicitRequestFixture(ExternalCapabilityExecutionMode.Interactive);
        fixture.Plan.InvocationAdmissions[0].NyxIdExplicitRequestGrant.RequestContractDigest = "digest-other";
        Rehash(fixture.Plan);

        var result = Check(fixture);

        result.Failure.Should().Be(
            WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionProofInvalid);
    }

    [Fact]
    public void CheckCompatibility_WithMalformedDurableOwner_ShouldReturnTypedFailure()
    {
        var fixture = ExplicitRequestFixture(ExternalCapabilityExecutionMode.Durable);
        fixture.Plan.DurableAuthorizationOwner.OwnerSubject = " caller-alpha";
        Rehash(fixture.Plan);

        var result = Check(fixture);

        result.Failure.Should().Be(
            WorkflowCapabilityAdmissionCompatibilityFailure.DurableOwnerInvalid);
    }

    [Fact]
    public void ValidateOrThrow_WithProofFailure_ShouldPreserveDetailedException()
    {
        var fixture = ExplicitRequestFixture(ExternalCapabilityExecutionMode.Interactive);
        fixture.Plan.InvocationAdmissions[0].NyxIdExplicitRequestGrant.RequestContractDigest = "digest-other";
        Rehash(fixture.Plan);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            fixture.Plan,
            fixture.WorkflowYaml,
            fixture.InlineWorkflowYamls,
            fixture.ExecutionMode,
            fixture.ExpectedInvocations,
            fixture.WorkflowId,
            fixture.RevisionId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit request grant scope is invalid*");
    }

    private static WorkflowCapabilityAdmissionCompatibilityResult Check(Fixture fixture) =>
        WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(
            fixture.Plan,
            fixture.WorkflowYaml,
            fixture.InlineWorkflowYamls,
            fixture.ExecutionMode,
            fixture.ExpectedInvocations,
            fixture.WorkflowId,
            fixture.RevisionId);

    private static Fixture HostFixture()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var expected = new[]
        {
            HostInvocation("wf-alpha/send", "send"),
            HostInvocation("wf-alpha/store", "store"),
        };
        var admissions = expected.Select(static invocation => new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = invocation.CallSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                HostConnector = invocation.Selector.HostConnector.Clone(),
            },
        });
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            admissions,
            [
                ConnectorSource(),
                Source(
                    ExternalCapabilitySourceKind.ConnectorCatalog,
                    "connector-catalog-beta",
                    0,
                    "connector-digest-beta"),
            ]);
        return new Fixture(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            expected,
            "wf-alpha",
            "rev-alpha");
    }

    private static Fixture ExplicitRequestFixture(ExternalCapabilityExecutionMode executionMode)
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "us-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        var selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = request.Clone() };
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.ReadOnly,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(request);
        var capability = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = request.Clone(),
                ServiceSlugSnapshot = "service-alpha",
                ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestProofDigest(
                    requestDigest,
                    "service-alpha"),
                ExecutionPolicy = policy,
            },
        };
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = "wf-alpha",
            RevisionId = "rev-alpha",
            CallSiteId = "wf-alpha/request",
            RequestContractDigest = requestDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "binder-alpha",
            Risk = NyxIdOperationRisk.ReadOnly,
        };
        grant.AllowedExecutionModes.Add(policy.AllowedExecutionModes);
        capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);
        var admission = new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = grant.CallSiteId,
            Capability = capability,
            NyxIdExplicitRequestGrant = grant,
        };
        var sources = new List<ExternalCapabilitySourceStamp> { ExplicitRequestSource() };
        ExternalCapabilityAuthorizationOwner? owner = null;
        if (executionMode == ExternalCapabilityExecutionMode.Durable)
        {
            sources.Add(DurableCatalogSource());
            owner = new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            };
        }
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            executionMode,
            [admission],
            sources,
            owner,
            "wf-alpha",
            "rev-alpha");
        return new Fixture(
            plan,
            yaml,
            new Dictionary<string, string>(),
            executionMode,
            [new ExternalToolInvocationSpec
            {
                CallSiteId = grant.CallSiteId,
                ToolName = "nyxid_proxy",
                Selector = selector,
            }],
            "wf-alpha",
            "rev-alpha");
    }

    private static ExternalToolInvocationSpec HostInvocation(string callSiteId, string operationId) => new()
    {
        CallSiteId = callSiteId,
        ToolName = "connector_call",
        Selector = new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-alpha",
                OperationId = operationId,
                ContractDigest = $"digest-{operationId}",
            },
        },
    };

    private static ExternalCapabilitySourceStamp ConnectorSource() => Source(
        ExternalCapabilitySourceKind.ConnectorCatalog,
        "connector-catalog-alpha",
        0,
        "connector-digest");

    private static ExternalCapabilitySourceStamp ExplicitRequestSource() => Source(
        ExternalCapabilitySourceKind.NyxIdUserServices,
        "nyxid-user-services-alpha",
        0,
        "user-services-digest");

    private static ExternalCapabilitySourceStamp DurableCatalogSource() => Source(
        ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
        "nyxid:personal:caller-alpha",
        17,
        "catalog-digest");

    private static ExternalCapabilitySourceStamp Source(
        ExternalCapabilitySourceKind kind,
        string sourceId,
        long version,
        string digest) => new()
        {
            SourceKind = kind,
            SourceId = sourceId,
            SourceVersion = version,
            ObservedAt = Timestamp.FromDateTimeOffset(Now),
            FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(5)),
            ContentDigest = digest,
        };

    private static void Rehash(WorkflowCapabilityAdmissionPlan plan) =>
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

    private sealed record Fixture(
        WorkflowCapabilityAdmissionPlan Plan,
        string WorkflowYaml,
        IReadOnlyDictionary<string, string> InlineWorkflowYamls,
        ExternalCapabilityExecutionMode ExecutionMode,
        IReadOnlyList<ExternalToolInvocationSpec> ExpectedInvocations,
        string WorkflowId,
        string RevisionId);
}
