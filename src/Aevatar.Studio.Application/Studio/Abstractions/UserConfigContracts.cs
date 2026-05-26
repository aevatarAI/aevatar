namespace Aevatar.Studio.Application.Studio.Abstractions;

public sealed record SaveUserConfigCommand(
    string? DefaultModel = null,
    string? PreferredLlmRoute = null,
    string? RuntimeMode = null,
    string? LocalRuntimeBaseUrl = null,
    string? RemoteRuntimeBaseUrl = null,
    string? GithubUsername = null,
    int? MaxToolRounds = null);

public interface IUserConfigService
{
    Task<UserConfig> GetAsync(CancellationToken ct = default);

    Task<UserConfig> SaveAsync(SaveUserConfigCommand command, CancellationToken ct = default);

    Task<UserConfig> SaveAsync(
        string? bearerToken,
        SaveUserConfigCommand command,
        CancellationToken ct = default);

    Task<UserConfig> SaveLlmPreferenceAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct = default);
}
