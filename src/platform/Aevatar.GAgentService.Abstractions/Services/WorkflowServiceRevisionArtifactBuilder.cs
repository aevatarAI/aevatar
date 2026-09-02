using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class WorkflowServiceRevisionArtifactBuilder
{
    public static PreparedServiceRevisionArtifact Build(
        ServiceRevisionSpec revisionSpec,
        string resolvedWorkflowName,
        WorkflowAuthorizationDependencies authorizationDependencies,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan)
    {
        ArgumentNullException.ThrowIfNull(revisionSpec);
        ArgumentNullException.ThrowIfNull(authorizationDependencies);
        ArgumentNullException.ThrowIfNull(capabilityAdmissionPlan);
        var identity = revisionSpec.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var workflowSpec = revisionSpec.WorkflowSpec
            ?? throw new InvalidOperationException("workflow implementation_spec is required.");
        if (authorizationDependencies.ServiceGrantPolicy == WorkflowServiceGrantPolicy.Unspecified ||
            !Enum.IsDefined(authorizationDependencies.ServiceGrantPolicy))
        {
            throw new InvalidOperationException("workflow authorization dependencies are required.");
        }

        if (capabilityAdmissionPlan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(capabilityAdmissionPlan.ExecutionMode))
        {
            throw new InvalidOperationException("workflow capability admission execution mode is required.");
        }

        if (workflowSpec.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(workflowSpec.ExpectedExecutionMode) ||
            workflowSpec.ExpectedExecutionMode != capabilityAdmissionPlan.ExecutionMode)
        {
            throw new InvalidOperationException(
                "workflow expected execution mode must match the capability admission plan.");
        }

        var admittedCapabilities =
            WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(capabilityAdmissionPlan);
        var authorizationEvidence = new WorkflowRevisionAuthorizationEvidence
        {
            OwnerLlmRouteRequired = authorizationDependencies.OwnerLlmRouteRequired,
            ServiceGrantRequirement = WorkflowServiceGrantRequirementClassifier.Classify(admittedCapabilities),
        };
        authorizationEvidence.ExternalCapabilities.Add(admittedCapabilities);

        var bindingIdentity = WorkflowCapabilityAdmissionPlanIntegrity
            .RequiresExplicitRequestBindingIdentity(capabilityAdmissionPlan)
            ? WorkflowServiceDeploymentPlanIntegrity.RequireExplicitBindingIdentity(
                workflowSpec.WorkflowId,
                revisionSpec.RevisionId)
            : (WorkflowServiceBindingIdentity?)null;

        var workflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = resolvedWorkflowName,
            WorkflowYaml = workflowSpec.WorkflowYaml,
            DefinitionActorId = workflowSpec.DefinitionActorId ?? string.Empty,
            AuthorizationEvidence = authorizationEvidence,
            CapabilityAdmissionPlan = capabilityAdmissionPlan.Clone(),
            ExecutionMode = capabilityAdmissionPlan.ExecutionMode,
        };
        workflowPlan.InlineWorkflowYamls.Add(workflowSpec.InlineWorkflowYamls);
        if (bindingIdentity is { } explicitBindingIdentity)
        {
            workflowPlan.WorkflowId = explicitBindingIdentity.WorkflowId;
            workflowPlan.RevisionId = explicitBindingIdentity.RevisionId;
        }

        return new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = revisionSpec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
                    ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
                    Description = "Workflow chat endpoint.",
                },
            },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = workflowPlan,
            },
        };
    }

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}
