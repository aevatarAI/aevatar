using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppResourceQueryApplicationService : IAppResourceQueryPort
{
    private readonly IAppRegistryReader _registryReader;

    public AppResourceQueryApplicationService(IAppRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public async Task<AppReleaseResourcesSnapshot?> GetReleaseResourcesAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default)
    {
        var release = await _registryReader.GetReleaseAsync(appId, releaseId, ct);
        if (release == null)
            return null;

        var snapshot = new AppReleaseResourcesSnapshot
        {
            AppId = release.AppId,
            ReleaseId = release.ReleaseId,
        };
        snapshot.ConnectorRefs.Add(release.ConnectorRefs);
        snapshot.SecretRefs.Add(release.SecretRefs);
        return snapshot;
    }
}
