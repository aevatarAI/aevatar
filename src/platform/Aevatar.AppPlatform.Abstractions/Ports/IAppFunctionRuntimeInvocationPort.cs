namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppFunctionRuntimeInvocationPort
{
    Task<AppFunctionRuntimeInvokeAccepted?> TryInvokeAsync(
        AppFunctionExecutionTarget target,
        AppFunctionInvokeRequest request,
        Func<AppFunctionRuntimeInvokeAccepted, CancellationToken, ValueTask<string>> onAcceptedAsync,
        CancellationToken ct = default);
}
