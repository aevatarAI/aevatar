using Aevatar.Maker.Sagas.States;

namespace Aevatar.Maker.Sagas.Queries;

public interface IMakerExecutionSagaQueryService
{
    Task<MakerExecutionSagaState?> GetAsync(string correlationId, CancellationToken ct = default);

    Task<IReadOnlyList<MakerExecutionSagaState>> ListAsync(int take = 50, CancellationToken ct = default);
}
