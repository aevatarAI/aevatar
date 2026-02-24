using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionLifecyclePort
{
    bool ProjectionEnabled { get; }

    Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default);

    Task AttachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task DetachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task ReleaseActorProjectionAsync(
        IWorkflowExecutionProjectionLease lease,
        CancellationToken ct = default);
}
