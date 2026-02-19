using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;

    public WorkflowRunActorResolver(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
    }

    public async Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        var requestedWorkflowName = string.IsNullOrWhiteSpace(request.WorkflowName) ? "direct" : request.WorkflowName;

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var existing = await _runtime.GetAsync(request.ActorId);
            if (existing == null)
                return new WorkflowActorResolutionResult(null, requestedWorkflowName, string.Empty, WorkflowChatRunStartError.AgentNotFound);

            if (existing.Agent is not WorkflowGAgent workflowAgent)
                return new WorkflowActorResolutionResult(null, requestedWorkflowName, string.Empty, WorkflowChatRunStartError.AgentTypeNotSupported);

            var snapshot = workflowAgent.GetWorkflowDefinitionSnapshot();
            var resolvedWorkflowNameForRun = string.IsNullOrWhiteSpace(snapshot.WorkflowName)
                ? requestedWorkflowName
                : snapshot.WorkflowName;

            if (string.IsNullOrWhiteSpace(snapshot.WorkflowYaml))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    resolvedWorkflowNameForRun,
                    string.Empty,
                    WorkflowChatRunStartError.WorkflowNotFound);
            }

            return new WorkflowActorResolutionResult(
                existing,
                resolvedWorkflowNameForRun,
                snapshot.WorkflowYaml,
                WorkflowChatRunStartError.None);
        }

        var workflowNameForRun = requestedWorkflowName;
        var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
        if (yaml == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, string.Empty, WorkflowChatRunStartError.WorkflowNotFound);

        var actor = await _runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        if (actor.Agent is WorkflowGAgent createdWorkflowAgent)
            createdWorkflowAgent.ConfigureWorkflow(yaml, workflowNameForRun);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, yaml, WorkflowChatRunStartError.None);
    }
}
