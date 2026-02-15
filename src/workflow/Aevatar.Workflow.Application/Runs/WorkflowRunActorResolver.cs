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
        var workflowNameForRun = string.IsNullOrWhiteSpace(request.WorkflowName) ? "direct" : request.WorkflowName;

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var existing = await _runtime.GetAsync(request.ActorId);
            if (existing == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

            if (existing.Agent is not WorkflowGAgent)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

            return new WorkflowActorResolutionResult(existing, workflowNameForRun, WorkflowChatRunStartError.None);
        }

        var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
        if (yaml == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

        var actor = await _runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        if (actor.Agent is WorkflowGAgent workflowAgent)
            workflowAgent.ConfigureWorkflow(yaml, workflowNameForRun);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }
}
