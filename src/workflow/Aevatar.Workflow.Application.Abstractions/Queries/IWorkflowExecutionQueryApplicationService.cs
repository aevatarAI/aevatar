using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowExecutionQueryApplicationService
{
    bool WorkflowActorCurrentStateQueryEnabled { get; }

    Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default);

    IReadOnlyList<string> ListWorkflows();

    Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default);

    Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default);

    Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default);

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: application query contract exposed actor snapshot inspection by actorId.
    //   New principle: application query contract exposes workflow actor current-state readmodel lookup by actorId.
    Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default);

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string workflowRunId, int take = 200, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default);
}
