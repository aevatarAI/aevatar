namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowExecutionQueryApplicationService
{
    bool ActorQueryEnabled { get; }

    Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default);

    IReadOnlyList<string> ListWorkflows();

    IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog();

    WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName);

    WorkflowCapabilitiesDocument GetCapabilities();

    Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default);

    Task<WorkflowRunReport?> GetActorReportAsync(string actorId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default);
}
