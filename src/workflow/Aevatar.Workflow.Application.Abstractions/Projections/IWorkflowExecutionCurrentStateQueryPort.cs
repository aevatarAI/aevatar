using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionCurrentStateQueryPort
{
    bool EnableActorQueryEndpoints { get; }

    Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default);
}
