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
        CancellationToken ct = default) =>
        throw new NotSupportedException("Typed UserConfig updates are not implemented by this adapter.");

    Task<UserConfigSaveReceipt> SaveAsync(UserConfig config, CancellationToken ct = default);

    Task<UserConfigSaveReceipt> SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default);

    Task<UserConfigSaveReceipt> SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default);
}
