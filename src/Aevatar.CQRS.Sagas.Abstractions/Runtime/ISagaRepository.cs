using Aevatar.CQRS.Sagas.Abstractions.State;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaRepository
{
    Task<ISagaState?> LoadAsync(
        string sagaName,
        string correlationId,
        Type stateType,
        CancellationToken ct = default);

    Task SaveAsync(
        string sagaName,
        ISagaState state,
        Type stateType,
        int? expectedVersion = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ISagaState>> ListAsync(
        string sagaName,
        Type stateType,
        int take = 100,
        CancellationToken ct = default);
}
