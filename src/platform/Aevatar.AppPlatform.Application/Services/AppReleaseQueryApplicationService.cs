using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppReleaseQueryApplicationService : IAppReleaseQueryPort
{
    private readonly IAppRegistryReader _registryReader;

    public AppReleaseQueryApplicationService(IAppRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public Task<IReadOnlyList<AppReleaseSnapshot>> ListAsync(string appId, CancellationToken ct = default) =>
        _registryReader.ListReleasesAsync(appId, ct);

    public Task<AppReleaseSnapshot?> GetAsync(string appId, string releaseId, CancellationToken ct = default) =>
        _registryReader.GetReleaseAsync(appId, releaseId, ct);
}
