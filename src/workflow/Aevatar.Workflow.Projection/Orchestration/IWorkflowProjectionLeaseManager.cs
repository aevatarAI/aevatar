namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionLeaseManager
{
    Task AcquireAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default);
}
