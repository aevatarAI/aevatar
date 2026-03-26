namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppRegistryWriter
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

    Task<AppReleaseSnapshot> UpdateReleaseStatusAsync(
        string appId,
        string releaseId,
        AppReleaseStatus status,
        CancellationToken ct = default);

    Task<AppEntryRef> UpsertEntryAsync(
        string appId,
        string releaseId,
        AppEntryRef entry,
        CancellationToken ct = default);

    Task<bool> DeleteEntryAsync(
        string appId,
        string releaseId,
        string entryId,
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
