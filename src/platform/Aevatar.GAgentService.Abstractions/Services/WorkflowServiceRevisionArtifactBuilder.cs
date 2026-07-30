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

        var admittedCapabilities =
            WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(capabilityAdmissionPlan);
        var authorizationEvidence = new WorkflowRevisionAuthorizationEvidence
        {
            OwnerLlmRouteRequired = authorizationDependencies.OwnerLlmRouteRequired,
            ServiceGrantRequirement = WorkflowServiceGrantRequirementClassifier.Classify(admittedCapabilities),
        };
        authorizationEvidence.ExternalCapabilities.Add(admittedCapabilities);

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
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = resolvedWorkflowName,
                    WorkflowYaml = workflowSpec.WorkflowYaml,
                    DefinitionActorId = workflowSpec.DefinitionActorId ?? string.Empty,
                    InlineWorkflowYamls = { workflowSpec.InlineWorkflowYamls },
                    AuthorizationEvidence = authorizationEvidence,
                    CapabilityAdmissionPlan = capabilityAdmissionPlan.Clone(),
                },
            },
        };
    }

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}
