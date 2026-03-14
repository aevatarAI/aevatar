using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class WorkflowServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IWorkflowRunActorPort _workflowRunActorPort;

    public WorkflowServiceImplementationAdapter(IWorkflowRunActorPort workflowRunActorPort)
    {
        _workflowRunActorPort = workflowRunActorPort ?? throw new ArgumentNullException(nameof(workflowRunActorPort));
    }

    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Workflow;

    public async Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.WorkflowSpec
            ?? throw new InvalidOperationException("workflow implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(spec.WorkflowYaml))
            throw new InvalidOperationException("workflow_yaml is required.");

        var resolvedWorkflowName = spec.WorkflowName;
        if (string.IsNullOrWhiteSpace(resolvedWorkflowName))
        {
            var parse = await _workflowRunActorPort.ParseWorkflowYamlAsync(spec.WorkflowYaml, ct);
            if (!parse.Succeeded)
                throw new InvalidOperationException(parse.Error);

            resolvedWorkflowName = parse.WorkflowName;
        }

        return new PreparedServiceRevisionArtifact
        {
            Identity = request.Spec.Identity.Clone(),
            RevisionId = request.Spec.RevisionId,
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
                    WorkflowYaml = spec.WorkflowYaml,
                    DefinitionActorId = spec.DefinitionActorId ?? string.Empty,
                },
            },
        };
    }

    private static string GetTypeUrl(Google.Protobuf.Reflection.MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}
