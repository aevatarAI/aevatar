using Aevatar.Maker.Sagas.States;

namespace Aevatar.Maker.Sagas.Queries;

public sealed class MakerExecutionSagaQueryService : IMakerExecutionSagaQueryService
{
    private readonly ISagaRepository _repository;

    public MakerExecutionSagaQueryService(ISagaRepository repository)
    {
        _repository = repository;
    }

    public Task<MakerExecutionSagaState?> GetAsync(string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return Task.FromResult<MakerExecutionSagaState?>(null);

        return _repository.LoadAsync<MakerExecutionSagaState>(MakerExecutionSagaNames.Execution, correlationId, ct);
    }

    public Task<IReadOnlyList<MakerExecutionSagaState>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        return _repository.ListAsync<MakerExecutionSagaState>(MakerExecutionSagaNames.Execution, boundedTake, ct);
    }
}
