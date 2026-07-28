namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure-write command service for user configuration.
/// Dispatches commands to the UserConfigGAgent actor.
/// </summary>
public interface IUserConfigCommandService
{
    Task<UserConfigSaveReceipt> UpdateAsync(
        UserConfigResourceKey resource,
        UserConfigUpdate update,
        CancellationToken ct = default);
}
