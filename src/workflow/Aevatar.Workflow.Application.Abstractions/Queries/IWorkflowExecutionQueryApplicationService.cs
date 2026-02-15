namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowExecutionQueryApplicationService
{
    bool RunQueryEnabled { get; }

    Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default);

    IReadOnlyList<string> ListWorkflows();

    Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(int take = 50, CancellationToken ct = default);

    Task<WorkflowRunReport?> GetRunAsync(string runId, CancellationToken ct = default);
}
