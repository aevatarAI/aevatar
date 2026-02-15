using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Projection;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly IWorkflowExecutionReportMapper _reportMapper;

    public WorkflowExecutionQueryApplicationService(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionProjectionService projectionService,
        IWorkflowExecutionReportMapper reportMapper)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
        _projectionService = projectionService;
        _reportMapper = reportMapper;
    }

    public bool RunQueryEnabled => _projectionService.EnableRunQueryEndpoints;

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

        var reports = await _projectionService.ListRunsAsync(take, ct);
        return reports.Select(_reportMapper.ToSummary).ToList();
    }

    public async Task<WorkflowRunReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!RunQueryEnabled)
            return null;

        var report = await _projectionService.GetRunAsync(runId, ct);
        return report == null ? null : _reportMapper.ToReport(report);
    }
}
