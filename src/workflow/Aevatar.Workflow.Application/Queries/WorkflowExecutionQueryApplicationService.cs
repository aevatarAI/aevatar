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

    public async Task<WorkflowRunReport?> GetActorReportAsync(string actorId, CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints)
            return null;

        return await _artifactQueryPort.GetActorReportAsync(actorId, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints)
            return [];

        return await _artifactQueryPort.ListActorTimelineAsync(actorId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _artifactQueryPort.GetActorGraphEdgesAsync(actorId, take, options, ct);
    }

    public async Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_artifactQueryPort.EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId ?? string.Empty,
            };

        return await _artifactQueryPort.GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);
    }
}
