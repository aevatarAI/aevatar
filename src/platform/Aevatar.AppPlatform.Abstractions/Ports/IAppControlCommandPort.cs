namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppControlCommandPort
{
    Task<AppDefinitionSnapshot> CreateAppAsync(
        AppDefinitionSnapshot definition,
        CancellationToken ct = default);

    Task<AppDefinitionSnapshot> UpsertAppAsync(
        AppDefinitionSnapshot definition,
        CancellationToken ct = default);

    Task<AppDefinitionSnapshot> SetDefaultReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);

    Task<AppReleaseSnapshot> UpsertReleaseAsync(
        AppReleaseSnapshot release,
        CancellationToken ct = default);

    Task<AppReleaseSnapshot> PublishReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);

    Task<AppReleaseSnapshot> ArchiveReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);

    Task<AppEntryRef> UpsertFunctionAsync(
        string appId,
        string releaseId,
        AppEntryRef functionRef,
        CancellationToken ct = default);

    Task<bool> DeleteFunctionAsync(
        string appId,
        string releaseId,
        string functionId,
        CancellationToken ct = default);

    Task<AppReleaseResourcesSnapshot> ReplaceReleaseResourcesAsync(
        AppReleaseResourcesSnapshot resources,
        CancellationToken ct = default);

    Task<AppRouteSnapshot> UpsertRouteAsync(
        AppRouteSnapshot route,
        CancellationToken ct = default);

    Task<bool> DeleteRouteAsync(
        string appId,
        string routePath,
        CancellationToken ct = default);
}
