using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class WorkflowServiceRevisionArtifactBuilderTests
{
    [Fact]
    public void Build_ShouldIncludeResolvableChatProtocolDescriptors()
    {
        var artifact = BuildArtifact();

        artifact.ProtocolDescriptorSet.IsEmpty.Should().BeFalse();
        ResolveDescriptor(artifact.ProtocolDescriptorSet, ChatRequestEvent.Descriptor.FullName)
            .Should().NotBeNull();
        ResolveDescriptor(artifact.ProtocolDescriptorSet, ChatResponseEvent.Descriptor.FullName)
            .Should().NotBeNull();
    }

    [Fact]
    public void Build_WithExplicitRequestCapability_ShouldRequireServiceGrant()
    {
        var artifact = BuildArtifact(ExplicitRequestCapability("usvc-explicit-alpha"));

        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.Required);
        artifact.DeploymentPlan.WorkflowPlan.WorkflowId.Should().Be("wf-artifact-alpha");
        artifact.DeploymentPlan.WorkflowPlan.RevisionId.Should().Be("rev-artifact-alpha");
        artifact.DeploymentPlan.WorkflowPlan.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        artifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
    }

    [Fact]
    public void Build_WithPublishedOperationCapability_ShouldKeepWorkflowBindingIdentity()
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
        artifact.DeploymentPlan.WorkflowPlan.WorkflowId.Should().Be("wf-artifact-alpha");
        artifact.DeploymentPlan.WorkflowPlan.RevisionId.Should().Be("rev-artifact-alpha");
        new PreparedServiceRevisionArtifactAssembler().Assemble(artifact).ArtifactHash.Should()
            .NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Build_WithExplicitRequestCapabilityAndMissingWorkflowId_ShouldFailClosed()
    {
        var action = () => BuildArtifactWithIdentity(
            workflowId: string.Empty,
            revisionId: "rev-artifact-alpha",
            ExplicitRequestCapability("usvc-explicit-alpha"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*workflow_id*");
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

    [Fact]
    public void Build_WithUnknownCapability_ShouldFailClosed()
    {
        var action = () => BuildArtifact(new ExternalWorkflowCapabilityRef());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*capability*");
    }

    [Fact]
    public void Build_WithKnownAndUnknownCapabilities_ShouldFailClosed()
    {
        var action = () => BuildArtifact(
            new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "usvc-published-alpha",
                },
            },
            new ExternalWorkflowCapabilityRef());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*capability*");
    }

    [Fact]
    public void Build_WithUnspecifiedExecutionMode_ShouldFailClosed()
    {
        var plan = new WorkflowCapabilityAdmissionPlan();

        var action = () => BuildArtifactWithIdentity(
            "wf-artifact-alpha",
            "rev-artifact-alpha",
            plan,
            ExternalCapabilityExecutionMode.Durable);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*");
    }

    [Fact]
    public void Build_WhenExpectedModeDiffersFromAdmissionPlan_ShouldFailClosed()
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
        };

        var action = () => BuildArtifactWithIdentity(
            "wf-artifact-alpha",
            "rev-artifact-alpha",
            plan,
            ExternalCapabilityExecutionMode.Interactive);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*match*");
    }

    [Fact]
    public void Build_WithoutCurrentToolCatalogPolicy_ShouldFailClosed()
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
        };
        var revisionSpec = CreateRevisionSpec(
            "wf-artifact-alpha",
            "rev-artifact-alpha",
            ExternalCapabilityExecutionMode.Durable);
        revisionSpec.WorkflowSpec.ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.LegacyV0;

        var action = () => WorkflowServiceRevisionArtifactBuilder.Build(
            revisionSpec,
            "artifact-workflow",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
            },
            plan);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*current reviewed policy*");
    }

    [Fact]
    public void ResolveBindingIdentity_WhenDeploymentModeDiffersFromAdmissionPlan_ShouldFailClosed()
    {
        var artifact = BuildArtifact();
        artifact.DeploymentPlan.WorkflowPlan.ExecutionMode = ExternalCapabilityExecutionMode.Interactive;

        var action = () => WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact,
            "rev-artifact-alpha");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*match*");
    }

    [Fact]
    public void ResolveBindingIdentity_WithUnspecifiedDeploymentMode_ShouldFailClosed()
    {
        var artifact = BuildArtifact();
        artifact.DeploymentPlan.WorkflowPlan.ExecutionMode = ExternalCapabilityExecutionMode.Unspecified;

        var action = () => WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact,
            "rev-artifact-alpha");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*match*");
    }

    [Fact]
    public void ResolveBindingIdentity_WithLegacyToolCatalogPolicy_ShouldRequireReissue()
    {
        var artifact = BuildArtifact();
        artifact.DeploymentPlan.WorkflowPlan.ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.LegacyV0;

        var action = () => WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact,
            "rev-artifact-alpha");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*reissued*current tool catalog policy*");
    }

    private static PreparedServiceRevisionArtifact BuildArtifact(
        params ExternalWorkflowCapabilityRef[] capabilities) =>
        BuildArtifactWithIdentity("wf-artifact-alpha", "rev-artifact-alpha", capabilities);

    private static PreparedServiceRevisionArtifact BuildArtifactWithIdentity(
        string workflowId,
        string revisionId,
        params ExternalWorkflowCapabilityRef[] capabilities)
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
        };
        for (var index = 0; index < capabilities.Length; index++)
        {
            var capability = capabilities[index];
            plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = $"wf-artifact-alpha/call-{index}",
                Capability = capability,
                NyxIdExplicitRequestGrant = capability.CapabilityCase ==
                                            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest
                    ? new NyxIdExplicitRequestGrant()
                    : null,
            });
        }
        return WorkflowServiceRevisionArtifactBuilder.Build(
            CreateRevisionSpec(
                workflowId,
                revisionId,
                ExternalCapabilityExecutionMode.Durable),
            "artifact-workflow",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
            },
            plan);
    }

    private static PreparedServiceRevisionArtifact BuildArtifactWithIdentity(
        string workflowId,
        string revisionId,
        WorkflowCapabilityAdmissionPlan plan,
        ExternalCapabilityExecutionMode expectedExecutionMode) =>
        WorkflowServiceRevisionArtifactBuilder.Build(
            CreateRevisionSpec(workflowId, revisionId, expectedExecutionMode),
            "artifact-workflow",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
            },
            plan);

    private static ServiceRevisionSpec CreateRevisionSpec(
        string workflowId,
        string revisionId,
        ExternalCapabilityExecutionMode expectedExecutionMode) =>
        new()
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-artifact-alpha",
                AppId = "app-artifact-alpha",
                Namespace = "namespace-artifact-alpha",
                ServiceId = "svc-published-runtime-alpha",
            },
            RevisionId = revisionId,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                WorkflowName = "artifact-workflow",
                WorkflowYaml = "name: artifact-workflow",
                WorkflowId = workflowId,
                ExpectedExecutionMode = expectedExecutionMode,
            },
        };

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

    private static MessageDescriptor? ResolveDescriptor(ByteString descriptorSet, string fullName)
    {
        var fileDescriptorSet = FileDescriptorSet.Parser.ParseFrom(descriptorSet);
        var files = FileDescriptor.BuildFromByteStrings(
            fileDescriptorSet.File.Select(static file => file.ToByteString()));
        foreach (var file in files)
        {
            var descriptor = FindDescriptor(file.MessageTypes, fullName);
            if (descriptor is not null)
                return descriptor;
        }

        return null;
    }

    private static MessageDescriptor? FindDescriptor(
        IList<MessageDescriptor> messageTypes,
        string fullName)
    {
        foreach (var messageType in messageTypes)
        {
            if (string.Equals(messageType.FullName, fullName, StringComparison.Ordinal))
                return messageType;

            var nested = FindDescriptor(messageType.NestedTypes, fullName);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
