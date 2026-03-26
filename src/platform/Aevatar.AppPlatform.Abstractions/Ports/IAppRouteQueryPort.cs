namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppRouteQueryPort
{
    Task<IReadOnlyList<AppRouteSnapshot>> ListAsync(
        string appId,
        CancellationToken ct = default);

    Task<AppRouteResolution?> ResolveAsync(
        string routePath,
        CancellationToken ct = default);
}
