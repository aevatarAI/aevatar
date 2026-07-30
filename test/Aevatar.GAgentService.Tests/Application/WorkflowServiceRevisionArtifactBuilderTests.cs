using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class WorkflowServiceRevisionArtifactBuilderTests
{
    [Fact]
    public void Build_WithExplicitRequestCapability_ShouldRequireServiceGrant()
    {
        var artifact = BuildArtifact(ExplicitRequestCapability("usvc-explicit-alpha"));

        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.Required);
    }

    [Fact]
    public void Build_WithPublishedOperationCapability_ShouldKeepRequiringServiceGrant()
    {
        var artifact = BuildArtifact(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "usvc-published-alpha",
            },
        });

        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.Required);
    }

    [Fact]
    public void Build_WithoutExternalServiceCapability_ShouldNotRequireServiceGrant()
    {
        var artifact = BuildArtifact(new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-calendar-alpha",
                OperationId = "list-events",
            },
        });

        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.NotRequired);
    }

    private static PreparedServiceRevisionArtifact BuildArtifact(
        ExternalWorkflowCapabilityRef capability)
    {
        var plan = new WorkflowCapabilityAdmissionPlan();
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "wf-artifact-alpha/call-alpha",
            Capability = capability,
        });
        return WorkflowServiceRevisionArtifactBuilder.Build(
            new ServiceRevisionSpec
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-artifact-alpha",
                    AppId = "app-artifact-alpha",
                    Namespace = "namespace-artifact-alpha",
                    ServiceId = "svc-published-runtime-alpha",
                },
                RevisionId = "rev-artifact-alpha",
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = "wf-artifact-alpha",
                    WorkflowYaml = "name: wf-artifact-alpha",
                },
            },
            "wf-artifact-alpha",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
            },
            plan);
    }

    private static ExternalWorkflowCapabilityRef ExplicitRequestCapability(string userServiceId) =>
        new()
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
            },
        };
}
