using Aevatar.Platform.Sagas.States;

namespace Aevatar.Platform.Sagas.Queries;

public interface IPlatformCommandSagaQueryService
{
    Task<PlatformCommandSagaState?> GetAsync(string commandId, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformCommandSagaState>> ListAsync(int take = 50, CancellationToken ct = default);
}
