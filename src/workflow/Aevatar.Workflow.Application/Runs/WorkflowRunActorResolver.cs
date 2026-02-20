using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;

    public WorkflowRunActorResolver(
        IWorkflowRunActorPort actorPort,
        IWorkflowDefinitionRegistry workflowRegistry)
    {
        _actorPort = actorPort;
        _workflowRegistry = workflowRegistry;
    }

    public async Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        var requestedWorkflowName = NormalizeWorkflowName(request.WorkflowName);
        var workflowNameForRun = string.IsNullOrWhiteSpace(requestedWorkflowName) ? "direct" : requestedWorkflowName;

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var existing = await _actorPort.GetAsync(request.ActorId, ct);
            if (existing == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

            if (!_actorPort.IsWorkflowActor(existing))
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

            var boundWorkflowName = NormalizeWorkflowName(_actorPort.GetBoundWorkflowName(existing));
            if (string.IsNullOrWhiteSpace(boundWorkflowName))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    WorkflowChatRunStartError.AgentWorkflowNotConfigured);
            }

            if (!string.IsNullOrWhiteSpace(requestedWorkflowName) &&
                !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            return new WorkflowActorResolutionResult(existing, boundWorkflowName, WorkflowChatRunStartError.None);
        }

        var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
        if (yaml == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

        var actor = await _actorPort.CreateAsync(ct);
        _actorPort.ConfigureWorkflow(actor, yaml, workflowNameForRun);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
}
