using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

internal interface IWorkflowRunDetachedCleanupOutbox
{
    Task EnqueueAsync(
        WorkflowRunDetachedCleanupRequest request,
        CancellationToken ct = default);

    Task MarkDispatchAcceptedAsync(
        WorkflowRunDetachedCleanupDispatchAcceptedRequest request,
        CancellationToken ct = default);

    Task DiscardAsync(
        WorkflowRunDetachedCleanupDiscardRequest request,
        CancellationToken ct = default);

    Task TriggerReplayAsync(
        int batchSize,
        CancellationToken ct = default);
}
