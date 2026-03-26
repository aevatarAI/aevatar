namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppResourceQueryPort
{
    Task<AppReleaseResourcesSnapshot?> GetReleaseResourcesAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);
}
