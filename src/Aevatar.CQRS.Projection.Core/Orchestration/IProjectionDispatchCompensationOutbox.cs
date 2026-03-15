namespace Aevatar.CQRS.Projection.Core.Orchestration;

public interface IProjectionDispatchCompensationOutbox
{
    Task EnqueueAsync(
        ProjectionCompensationEnqueuedEvent evt,
        CancellationToken ct = default);

    Task TriggerReplayAsync(
        int batchSize,
        CancellationToken ct = default);
}
