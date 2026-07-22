namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure-read query port for user configuration.
/// Reads from the projection document store, not from actor state.
/// </summary>
public interface IUserConfigQueryPort
{
    Task<UserConfig> GetAsync(
        UserConfigResourceKey resource,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Typed UserConfig reads are not implemented by this adapter.");

    Task<UserConfig> GetAsync(CancellationToken ct = default);

    Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default);
}
