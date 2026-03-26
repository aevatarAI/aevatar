namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppRegistryReader
{
    Task<IReadOnlyList<AppDefinitionSnapshot>> ListAppsAsync(
        string? ownerScopeId = null,
        CancellationToken ct = default);

    Task<AppDefinitionSnapshot?> GetAppAsync(
        string appId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AppReleaseSnapshot>> ListReleasesAsync(
        string appId,
        CancellationToken ct = default);

    Task<AppReleaseSnapshot?> GetReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AppRouteSnapshot>> ListRoutesAsync(
        string appId,
        CancellationToken ct = default);

    Task<AppRouteSnapshot?> GetRouteByPathAsync(
        string routePath,
        CancellationToken ct = default);
}
