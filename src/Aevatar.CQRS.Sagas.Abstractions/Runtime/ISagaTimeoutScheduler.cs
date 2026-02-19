using Aevatar.CQRS.Sagas.Abstractions.Actions;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaTimeoutScheduler
{
    Task ScheduleAsync(
        string sagaName,
        string correlationId,
        string actorId,
        SagaScheduleTimeoutAction action,
        CancellationToken ct = default);
}
