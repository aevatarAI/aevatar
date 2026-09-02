using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService :
    IWorkflowExecutionQueryApplicationService,
    IWorkflowExecutionScopeQueryApplicationService
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

    public bool WorkflowActorCurrentStateQueryEnabled => _currentStateQueryPort.WorkflowActorCurrentStateQueryEnabled;

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.

    public async Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!WorkflowActorCurrentStateQueryEnabled)
            return [];

        var snapshots = await _currentStateQueryPort.ListWorkflowActorCurrentStatesAsync(ct: ct);
        return snapshots
            .Select(snapshot => new WorkflowAgentSummary(
                snapshot.ActorId,
                "WorkflowRunGAgent",
                $"WorkflowRunGAgent[{snapshot.WorkflowName}]"))
            .ToList();
    }

    public IReadOnlyList<string> ListWorkflows() => _workflowRegistry.GetNames();

    public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
        _workflowCatalogPort.ListWorkflowCatalogAsync(ct);

    public async Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        return await _workflowCatalogPort.GetWorkflowDetailAsync(workflowName, ct);
    }

    public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
        _workflowCapabilitiesPort.GetCapabilitiesAsync(ct);

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: application service exposed GetActorSnapshotAsync(actorId) as an actor inspection query.
    //   New principle: application service exposes GetWorkflowActorCurrentStateAsync(actorId) over the current-state readmodel.
    public async Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
    {
        if (!WorkflowActorCurrentStateQueryEnabled)
            return null;

        return await _currentStateQueryPort.GetWorkflowActorCurrentStateAsync(actorId, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default)
    {
        if (!WorkflowActorCurrentStateQueryEnabled)
            return [];

        return await _currentStateQueryPort.ListWorkflowActorCurrentStatesAsync(query, ct);
    }

    public async Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled)
            return null;

        return await _artifactQueryPort.GetWorkflowRunReportArtifactAsync(workflowRunId, ct);
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled)
            return [];

        return await _artifactQueryPort.ListWorkflowRunTimelineExportAsync(workflowRunId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
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
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
            return new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = workflowRunId ?? string.Empty,
            };

        return await _artifactQueryPort.GetWorkflowRunGraphExportSubgraphAsync(workflowRunId, depth, take, options, ct);
    }

    public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default) =>
        ResolveOwnedCurrentStateAsync(scopeId, actorId, ct);

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>?> ListWorkflowRunTimelineExportAsync(
        string scopeId,
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled ||
            await ResolveOwnedCurrentStateAsync(scopeId, workflowRunId, ct) == null)
        {
            return null;
        }

        return await _artifactQueryPort.ListWorkflowRunTimelineExportAsync(workflowRunId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>?> ListWorkflowRunGraphExportEdgesAsync(
        string scopeId,
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled ||
            await ResolveOwnedCurrentStateAsync(scopeId, workflowRunId, ct) == null)
        {
            return null;
        }

        return await _artifactQueryPort.GetWorkflowRunGraphExportEdgesAsync(workflowRunId, take, options, ct);
    }

    public async Task<WorkflowRunGraphExportSubgraph?> GetWorkflowRunGraphExportSubgraphAsync(
        string scopeId,
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.WorkflowArtifactQueryEnabled ||
            await ResolveOwnedCurrentStateAsync(scopeId, workflowRunId, ct) == null)
        {
            return null;
        }

        return await _artifactQueryPort.GetWorkflowRunGraphExportSubgraphAsync(workflowRunId, depth, take, options, ct);
    }

    private async Task<WorkflowActorSnapshot?> ResolveOwnedCurrentStateAsync(
        string scopeId,
        string actorId,
        CancellationToken ct)
    {
        if (!WorkflowActorCurrentStateQueryEnabled ||
            string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }

        var snapshot = await _currentStateQueryPort.GetWorkflowActorCurrentStateAsync(actorId, ct);
        return snapshot != null && string.Equals(
            snapshot.ScopeId?.Trim(),
            scopeId.Trim(),
            StringComparison.Ordinal)
            ? snapshot
            : null;
    }
}
