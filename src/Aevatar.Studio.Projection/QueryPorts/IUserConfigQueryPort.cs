using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Pure-read query port for user configuration.
/// Reads from the projection document store, not from actor state.
/// </summary>
public interface IUserConfigQueryPort
{
    Task<UserConfig> GetAsync(CancellationToken ct = default);
}
