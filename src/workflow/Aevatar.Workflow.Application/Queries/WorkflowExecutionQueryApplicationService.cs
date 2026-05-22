using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
{
    private readonly IWorkflowDefinitionCatalog _workflowRegistry;
    private readonly IWorkflowExecutionCurrentStateQueryPort _currentStateQueryPort;
    private readonly IWorkflowExecutionArtifactQueryPort _artifactQueryPort;
    private readonly IWorkflowCatalogPort _workflowCatalogPort;
    private readonly IWorkflowCapabilitiesPort _workflowCapabilitiesPort;

    public WorkflowExecutionQueryApplicationService(
        IWorkflowDefinitionCatalog workflowRegistry,
        IWorkflowExecutionCurrentStateQueryPort currentStateQueryPort,
        IWorkflowExecutionArtifactQueryPort artifactQueryPort,
        IWorkflowCatalogPort workflowCatalogPort,
        IWorkflowCapabilitiesPort workflowCapabilitiesPort)
    {
        _workflowRegistry = workflowRegistry ?? throw new ArgumentNullException(nameof(workflowRegistry));
        _currentStateQueryPort = currentStateQueryPort ?? throw new ArgumentNullException(nameof(currentStateQueryPort));
        _artifactQueryPort = artifactQueryPort ?? throw new ArgumentNullException(nameof(artifactQueryPort));
        _workflowCatalogPort = workflowCatalogPort ?? throw new ArgumentNullException(nameof(workflowCatalogPort));
        _workflowCapabilitiesPort = workflowCapabilitiesPort ?? throw new ArgumentNullException(nameof(workflowCapabilitiesPort));
    }

    public bool ActorQueryEnabled => _currentStateQueryPort.EnableActorQueryEndpoints;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.

    public async Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ActorQueryEnabled)
            return [];

        var snapshots = await _currentStateQueryPort.ListActorSnapshotsAsync(ct: ct);
        return snapshots
            .Select(snapshot => new WorkflowAgentSummary(
                snapshot.ActorId,
                "WorkflowRunGAgent",
                $"WorkflowRunGAgent[{snapshot.WorkflowName}]"))
            .ToList();
    }

    public IReadOnlyList<string> ListWorkflows() => _workflowRegistry.GetNames();

    public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog() =>
        _workflowCatalogPort.ListWorkflowCatalog();

    public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        return _workflowCatalogPort.GetWorkflowDetail(workflowName);
    }

    public WorkflowCapabilitiesDocument GetCapabilities() =>
        _workflowCapabilitiesPort.GetCapabilities();

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
    {
        if (!ActorQueryEnabled)
            return null;

        return await _currentStateQueryPort.GetActorSnapshotAsync(actorId, ct);
    }

    public async Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints)
            return null;

        return await _artifactQueryPort.GetWorkflowRunReportArtifactAsync(workflowRunId, ct);
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints)
            return [];

        return await _artifactQueryPort.ListWorkflowRunTimelineExportAsync(workflowRunId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(workflowRunId))
            return [];

        return await _artifactQueryPort.GetWorkflowRunGraphExportEdgesAsync(workflowRunId, take, options, ct);
    }

    public async Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(workflowRunId))
            return new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = workflowRunId ?? string.Empty,
            };

        return await _artifactQueryPort.GetWorkflowRunGraphExportSubgraphAsync(workflowRunId, depth, take, options, ct);
    }
}
