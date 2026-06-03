using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class WorkflowServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;

    public WorkflowServiceImplementationAdapter(IWorkflowDefinitionParser workflowDefinitionParser)
    {
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
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

        var parse = await _workflowDefinitionParser.ParseWorkflowYamlAsync(spec.WorkflowYaml, ct);
        if (!parse.Succeeded)
            throw new InvalidOperationException(parse.Error);

        var resolvedWorkflowName = spec.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedWorkflowName))
        {
            resolvedWorkflowName = parse.WorkflowName;
        }
        else if (!string.Equals(resolvedWorkflowName, parse.WorkflowName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("workflow_name must match workflow_yaml name.");
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
                    InlineWorkflowYamls = { spec.InlineWorkflowYamls },
                },
            },
        };
    }

    private static string GetTypeUrl(Google.Protobuf.Reflection.MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}
