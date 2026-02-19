using Aevatar.CQRS.Sagas.Abstractions.Actions;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaCommandEmitter
{
    Task EnqueueAsync(SagaEnqueueCommandAction action, CancellationToken ct = default);

    Task ScheduleAsync(SagaScheduleCommandAction action, CancellationToken ct = default);
}
