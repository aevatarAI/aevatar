using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
{
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionProjectionQueryPort _projectionPort;

    public WorkflowExecutionQueryApplicationService(
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionProjectionQueryPort projectionPort)
    {
        _workflowRegistry = workflowRegistry;
        _projectionPort = projectionPort;
    }

    public bool ActorQueryEnabled => _projectionPort.EnableActorQueryEndpoints;

    public async Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ActorQueryEnabled)
            return [];

        var snapshots = await _projectionPort.ListActorSnapshotsAsync(ct: ct);
        return snapshots
            .Select(snapshot => new WorkflowAgentSummary(
                snapshot.ActorId,
                "WorkflowRunGAgent",
                $"WorkflowRunGAgent[{snapshot.WorkflowName}]"))
            .ToList();
    }

    public IReadOnlyList<string> ListWorkflows() => _workflowRegistry.GetNames();

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
    {
        if (!ActorQueryEnabled)
            return null;

        return await _projectionPort.GetActorSnapshotAsync(actorId, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!ActorQueryEnabled)
            return [];

        return await _projectionPort.ListActorTimelineAsync(actorId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!ActorQueryEnabled || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _projectionPort.GetActorGraphEdgesAsync(actorId, take, options, ct);
    }

    public async Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!ActorQueryEnabled || string.IsNullOrWhiteSpace(actorId))
            return new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId ?? string.Empty,
            };

        return await _projectionPort.GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);
    }

    public async Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!ActorQueryEnabled || string.IsNullOrWhiteSpace(actorId))
            return null;

        return await _projectionPort.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, options, ct);
    }
}
