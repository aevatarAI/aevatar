namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure-write command service for user configuration.
/// Dispatches commands to the UserConfigGAgent actor.
/// </summary>
public interface IUserConfigCommandService
{
    Task SaveAsync(UserConfig config, CancellationToken ct = default);

    Task SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default);
}
