using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceInvokeReadinessEvaluatorTests
{
    private readonly ServiceInvokeReadinessEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_ShouldReturnReady_WhenActiveTargetRevisionAndPreparedEndpointExist()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = PreparedRevision(identity, "r1", "chat"),
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.Unspecified);
        entries[0].SelectedRevisionId.Should().Be("r1");
        entries[0].SelectedDeploymentId.Should().Be("dep-1");
        entries[0].SelectedActorId.Should().Be("actor-1");
    }

    [Fact]
    public void Evaluate_ShouldReturnServingTargetMissing_WhenNoActiveTargetMatchesEndpoint()
    {
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "other")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal));

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
        entries[0].SelectedRevisionId.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ServiceRevisionStatus.Created)]
    [InlineData(ServiceRevisionStatus.PreparationFailed)]
    [InlineData(ServiceRevisionStatus.Retired)]
    public void Evaluate_ShouldReturnRevisionNotPrepared_WhenSelectedRevisionIsNotPrepared(ServiceRevisionStatus status)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = new()
                {
                    Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                    Status = status,
                },
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.RevisionNotPrepared);
        entries[0].SelectedRevisionId.Should().Be("r1");
    }

    [Fact]
    public void Evaluate_ShouldReturnPreparedArtifactMissing_WhenArtifactDoesNotContainEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = PreparedRevision(identity, "r1", "other"),
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactMissing);
        entries[0].SelectedRevisionId.Should().Be("r1");
    }

    [Fact]
    public void Evaluate_ShouldReturnPreparedArtifactIncompatible_WhenWorkflowExecutionModeIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revision = PreparedWorkflowRevision(identity, "r1", "chat");
        revision.PreparedArtifact.DeploymentPlan.WorkflowPlan.ExecutionMode =
            ExternalCapabilityExecutionMode.Unspecified;

        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = revision,
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should()
            .Be(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible);
        entries[0].SelectedRevisionId.Should().Be("r1");
        entries[0].SelectedDeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public void Evaluate_ShouldReturnPreparedArtifactIncompatible_WhenWorkflowAdmissionPlanIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revision = PreparedWorkflowRevision(identity, "r1", "chat");
        revision.PreparedArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan = null;

        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = revision,
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should()
            .Be(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible);
    }

    [Fact]
    public void Evaluate_ShouldReturnPreparedArtifactIncompatible_WhenWorkflowAdmissionPlanRequiresRebind()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revision = PreparedWorkflowRevision(identity, "r1", "chat");
        revision.PreparedArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.SchemaVersion =
            WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion;

        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = revision,
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should()
            .Be(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible);
        entries[0].SelectedRevisionId.Should().Be("r1");
    }

    [Fact]
    public void Evaluate_ShouldOnlyUseCanonicalStatusesAndReasons()
    {
        var statuses = Enum.GetNames<ServiceInvokeReadinessStatus>();
        var reasons = Enum.GetNames<ServiceInvokeUnavailableReason>();

        statuses.Should().BeEquivalentTo("Unspecified", "Ready", "Unavailable");
        reasons.Should().BeEquivalentTo(
            "Unspecified",
            "ServingTargetMissing",
            "PreparedArtifactMissing",
            "RevisionNotPrepared",
            "PreparedArtifactIncompatible");
    }

    private static ServiceServingTargetSpec Target(
        string deploymentId,
        string revisionId,
        string actorId,
        params string[] endpointIds)
    {
        var target = new ServiceServingTargetSpec
        {
            DeploymentId = deploymentId,
            RevisionId = revisionId,
            PrimaryActorId = actorId,
            AllocationWeight = 100,
            ServingState = ServiceServingState.Active,
        };
        target.EnabledEndpointIds.Add(endpointIds);
        return target;
    }

    private static ServiceRevisionRecordState PreparedRevision(
        ServiceIdentity identity,
        string revisionId,
        params string[] endpointIds) =>
        new()
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
            Status = ServiceRevisionStatus.Prepared,
            PreparedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                revisionId,
                endpointIds
                    .Select(endpointId => GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: endpointId))
                    .ToArray()),
        };

    private static ServiceRevisionRecordState PreparedWorkflowRevision(
        ServiceIdentity identity,
        string revisionId,
        params string[] endpointIds) =>
        new()
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow-alpha",
                    WorkflowYaml = "name: workflow-alpha\nsteps: []",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
            Status = ServiceRevisionStatus.Published,
            PreparedArtifact = new PreparedServiceRevisionArtifact
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    endpointIds.Select(endpointId =>
                        GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: endpointId)),
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                        WorkflowName = "workflow-alpha",
                        WorkflowYaml = "name: workflow-alpha\nsteps: []",
                        ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                        CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                        {
                            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
                            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                        },
                    },
                },
            },
        };
}
