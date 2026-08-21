using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class WorkflowServiceRevisionArtifactBuilder
{
    private static readonly ByteString ChatProtocolDescriptorSet = BuildProtocolDescriptorSet(
        ChatRequestEvent.Descriptor,
        ChatResponseEvent.Descriptor);

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
        if (!WorkflowToolCatalogPolicies.IsCurrent(workflowSpec.ToolCatalogPolicyVersion))
        {
            throw new InvalidOperationException(
                "workflow tool catalog policy version must be the current reviewed policy.");
        }
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

        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.RequireExplicitBindingIdentity(
            workflowSpec.WorkflowId,
            revisionSpec.RevisionId);

        var workflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = resolvedWorkflowName,
            WorkflowYaml = workflowSpec.WorkflowYaml,
            WorkflowId = bindingIdentity.WorkflowId,
            RevisionId = bindingIdentity.RevisionId,
            DefinitionActorId = workflowSpec.DefinitionActorId ?? string.Empty,
            AuthorizationEvidence = authorizationEvidence,
            CapabilityAdmissionPlan = capabilityAdmissionPlan.Clone(),
            ExecutionMode = capabilityAdmissionPlan.ExecutionMode,
            ToolCatalogPolicyVersion = workflowSpec.ToolCatalogPolicyVersion,
        };
        workflowPlan.InlineWorkflowYamls.Add(workflowSpec.InlineWorkflowYamls);

        return new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = revisionSpec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            ProtocolDescriptorSet = ChatProtocolDescriptorSet,
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

    private static ByteString BuildProtocolDescriptorSet(params MessageDescriptor[] descriptors)
    {
        var files = new List<FileDescriptorProto>();
        var addedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
            AddFile(descriptor.File, addedFiles, files);

        var descriptorSet = new FileDescriptorSet();
        descriptorSet.File.Add(files);
        return descriptorSet.ToByteString();
    }

    private static void AddFile(
        FileDescriptor file,
        ISet<string> addedFiles,
        ICollection<FileDescriptorProto> files)
    {
        if (!addedFiles.Add(file.Name))
            return;

        foreach (var dependency in file.Dependencies)
            AddFile(dependency, addedFiles, files);

        files.Add(file.ToProto());
    }
}
