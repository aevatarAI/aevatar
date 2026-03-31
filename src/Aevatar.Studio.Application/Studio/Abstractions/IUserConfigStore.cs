namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IUserConfigStore
{
    Task<UserConfig> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default);
}

public sealed record UserConfig(string DefaultModel, string RuntimeBaseUrl = "");
