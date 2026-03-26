namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppReleaseQueryPort
{
    Task<IReadOnlyList<AppReleaseSnapshot>> ListAsync(
        string appId,
        CancellationToken ct = default);

    Task<AppReleaseSnapshot?> GetAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default);
}
