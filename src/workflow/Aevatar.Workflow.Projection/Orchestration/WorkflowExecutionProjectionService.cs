using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Application-facing facade for workflow run projection lifecycle and read-model queries.
/// </summary>
public sealed class WorkflowExecutionProjectionService : IWorkflowExecutionProjectionPort
{
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly IWorkflowProjectionQueryReader _queryReader;
    private readonly IWorkflowProjectionActivationService _activationService;
    private readonly IWorkflowProjectionReleaseService _releaseService;
    private readonly IWorkflowProjectionSinkSubscriptionManager _sinkSubscriptionManager;
    private readonly IWorkflowProjectionLiveSinkForwarder _liveSinkForwarder;

    public WorkflowExecutionProjectionService(
        WorkflowExecutionProjectionOptions options,
        IWorkflowProjectionQueryReader queryReader,
        IWorkflowProjectionActivationService activationService,
        IWorkflowProjectionReleaseService releaseService,
        IWorkflowProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IWorkflowProjectionLiveSinkForwarder liveSinkForwarder)
    {
        _options = options;
        _queryReader = queryReader;
        _activationService = activationService;
        _releaseService = releaseService;
        _sinkSubscriptionManager = sinkSubscriptionManager;
        _liveSinkForwarder = liveSinkForwarder;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableActorQueryEndpoints => _options.Enabled && _options.EnableActorQueryEndpoints;

    public async Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId))
            return null;

        return await _activationService.EnsureAsync(
            rootActorId,
            workflowName,
            input,
            commandId,
            ct);
    }

    public async Task AttachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _sinkSubscriptionManager.AttachOrReplaceAsync(
            runtimeLease,
            sink,
            evt => _liveSinkForwarder.ForwardAsync(runtimeLease, sink, evt, CancellationToken.None),
            ct);
    }

    public async Task DetachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _sinkSubscriptionManager.DetachAsync(runtimeLease, sink, ct);
    }

    public async Task ReleaseActorProjectionAsync(
        IWorkflowExecutionProjectionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();
        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _releaseService.ReleaseIfIdleAsync(runtimeLease, ct);
    }

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        return await _queryReader.GetActorSnapshotAsync(actorId, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints)
            return [];

        return await _queryReader.ListActorSnapshotsAsync(take, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _queryReader.ListActorTimelineAsync(actorId, take, ct);
    }

    public async Task<IReadOnlyList<WorkflowActorRelationItem>> GetActorRelationsAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _queryReader.GetActorRelationsAsync(actorId, take, ct);
    }

    public async Task<WorkflowActorRelationSubgraph> GetActorRelationSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
        {
            return new WorkflowActorRelationSubgraph
            {
                RootNodeId = actorId ?? string.Empty,
            };
        }

        return await _queryReader.GetActorRelationSubgraphAsync(actorId, depth, take, ct);
    }

    private static WorkflowExecutionRuntimeLease ResolveRuntimeLease(IWorkflowExecutionProjectionLease lease) =>
        lease as WorkflowExecutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported workflow projection lease implementation.");
}
