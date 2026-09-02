using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowExecutionScopeQueryApplicationService
{
    Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunTimelineExportItem>?> ListWorkflowRunTimelineExportAsync(
        string scopeId,
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunGraphExportEdge>?> ListWorkflowRunGraphExportEdgesAsync(
        string scopeId,
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowRunGraphExportSubgraph?> GetWorkflowRunGraphExportSubgraphAsync(
        string scopeId,
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default);
}
