using Aevatar.Platform.Application.Abstractions.Commands;

namespace Aevatar.Platform.Application.Abstractions.Queries;

public interface IPlatformCommandQueryApplicationService
{
    Task<PlatformCommandStatus?> GetByCommandIdAsync(string commandId, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformCommandStatus>> ListAsync(int take = 50, CancellationToken ct = default);
}
