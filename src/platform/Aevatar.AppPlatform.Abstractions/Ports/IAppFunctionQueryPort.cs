namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppFunctionQueryPort
{
    Task<IReadOnlyList<AppFunctionDescriptor>> ListAsync(
        string appId,
        string? releaseId = null,
        CancellationToken ct = default);

    Task<AppFunctionDescriptor?> GetAsync(
        string appId,
        string functionId,
        string? releaseId = null,
        CancellationToken ct = default);
}
