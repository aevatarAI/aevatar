using Aevatar.CQRS.Sagas.Abstractions.State;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISaga
{
    string Name { get; }

    Type StateType { get; }

    ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default);

    ValueTask<bool> CanStartAsync(EventEnvelope envelope, CancellationToken ct = default);

    ISagaState CreateNewState(string correlationId, EventEnvelope envelope);

    ValueTask HandleAsync(
        ISagaState state,
        EventEnvelope envelope,
        ISagaActionSink actions,
        CancellationToken ct = default);
}
