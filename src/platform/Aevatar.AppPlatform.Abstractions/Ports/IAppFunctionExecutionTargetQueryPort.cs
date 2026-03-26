namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppFunctionExecutionTargetQueryPort
{
    Task<AppFunctionExecutionTarget?> ResolveAsync(
        string appId,
        string functionId,
        string? releaseId = null,
        CancellationToken ct = default);
}
