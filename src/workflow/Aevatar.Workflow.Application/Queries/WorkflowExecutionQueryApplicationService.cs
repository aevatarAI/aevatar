using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;

    public WorkflowExecutionQueryApplicationService(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionProjectionPort projectionPort)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
        _projectionPort = projectionPort;
    }

    public bool RunQueryEnabled => _projectionPort.EnableRunQueryEndpoints;

    public async Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actors = await _runtime.GetAllAsync();
        var result = new List<WorkflowAgentSummary>(actors.Count);

        foreach (var actor in actors)
        {
            var description = await actor.Agent.GetDescriptionAsync();
            result.Add(new WorkflowAgentSummary(actor.Id, actor.Agent.GetType().Name, description));
        }

        return result;
    }

    public IReadOnlyList<string> ListWorkflows() => _workflowRegistry.GetNames();

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!RunQueryEnabled)
            return [];

        return await _projectionPort.ListRunsAsync(take, ct);
    }

    public async Task<WorkflowRunReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!RunQueryEnabled)
            return null;

        return await _projectionPort.GetRunAsync(runId, ct);
    }
}
