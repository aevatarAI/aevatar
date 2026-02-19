using Aevatar.Platform.Sagas.States;

namespace Aevatar.Platform.Sagas.Queries;

public sealed class PlatformCommandSagaQueryService : IPlatformCommandSagaQueryService
{
    private readonly ISagaRepository _repository;

    public PlatformCommandSagaQueryService(ISagaRepository repository)
    {
        _repository = repository;
    }

    public Task<PlatformCommandSagaState?> GetAsync(string commandId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return Task.FromResult<PlatformCommandSagaState?>(null);

        return _repository.LoadAsync<PlatformCommandSagaState>(PlatformCommandSagaNames.CommandLifecycle, commandId, ct);
    }

    public Task<IReadOnlyList<PlatformCommandSagaState>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        return _repository.ListAsync<PlatformCommandSagaState>(PlatformCommandSagaNames.CommandLifecycle, boundedTake, ct);
    }
}
