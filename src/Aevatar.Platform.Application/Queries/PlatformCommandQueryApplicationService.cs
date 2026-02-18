using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Aevatar.Platform.Application.Abstractions.Queries;

namespace Aevatar.Platform.Application.Queries;

public sealed class PlatformCommandQueryApplicationService : IPlatformCommandQueryApplicationService
{
    private readonly IPlatformCommandStateStore _store;

    public PlatformCommandQueryApplicationService(IPlatformCommandStateStore store)
    {
        _store = store;
    }

    public Task<PlatformCommandStatus?> GetByCommandIdAsync(string commandId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return Task.FromResult<PlatformCommandStatus?>(null);

        return _store.GetAsync(commandId, ct);
    }

    public Task<IReadOnlyList<PlatformCommandStatus>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        return _store.ListAsync(boundedTake, ct);
    }
}
