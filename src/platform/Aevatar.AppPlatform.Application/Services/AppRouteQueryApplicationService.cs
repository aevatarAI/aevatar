using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Core.Services;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppRouteQueryApplicationService : IAppRouteQueryPort
{
    private readonly IAppRegistryReader _registryReader;

    public AppRouteQueryApplicationService(IAppRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public Task<IReadOnlyList<AppRouteSnapshot>> ListAsync(string appId, CancellationToken ct = default) =>
        _registryReader.ListRoutesAsync(appId, ct);

    public async Task<AppRouteResolution?> ResolveAsync(string routePath, CancellationToken ct = default)
    {
        var normalizedRoutePath = AppRoutePathNormalizer.NormalizeRequired(routePath, nameof(routePath));
        var route = await _registryReader.GetRouteByPathAsync(normalizedRoutePath, ct);
        if (route == null)
            return null;

        var app = await _registryReader.GetAppAsync(route.AppId, ct);
        if (app == null)
            return null;

        var release = await _registryReader.GetReleaseAsync(route.AppId, route.ReleaseId, ct);
        if (release == null)
            return null;

        var entryRef = release.EntryRefs.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x.EntryId) &&
                                                                    !string.IsNullOrWhiteSpace(x.ServiceId));
        if (!string.IsNullOrWhiteSpace(route.EntryId))
        {
            entryRef = release.EntryRefs.FirstOrDefault(x => string.Equals(x.EntryId, route.EntryId, StringComparison.Ordinal));
        }

        if (entryRef == null)
            return null;

        var serviceRef = release.ServiceRefs.FirstOrDefault(x =>
            string.Equals(x.ServiceId, entryRef.ServiceId, StringComparison.Ordinal));
        if (serviceRef == null)
            return null;

        return new AppRouteResolution
        {
            RoutePath = route.RoutePath,
            App = app,
            Release = release,
            Entry = new AppResolvedEntryPoint
            {
                EntryId = entryRef.EntryId,
                EndpointId = entryRef.EndpointId,
                ServiceRef = serviceRef,
            },
        };
    }
}
