using Aevatar.Platform.Application.Abstractions.Commands;

namespace Aevatar.Platform.Application.Abstractions.Ports;

public interface IPlatformCommandStateStore
{
    Task UpsertAsync(PlatformCommandStatus status, CancellationToken ct = default);

    Task<PlatformCommandStatus?> GetAsync(string commandId, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformCommandStatus>> ListAsync(int take = 50, CancellationToken ct = default);
}
