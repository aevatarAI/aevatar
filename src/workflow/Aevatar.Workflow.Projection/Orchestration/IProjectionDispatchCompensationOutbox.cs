using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

internal interface IProjectionDispatchCompensationOutbox
{
    Task EnqueueAsync(
        ProjectionCompensationEnqueuedEvent evt,
        CancellationToken ct = default);

    Task TriggerReplayAsync(
        int batchSize,
        CancellationToken ct = default);
}
