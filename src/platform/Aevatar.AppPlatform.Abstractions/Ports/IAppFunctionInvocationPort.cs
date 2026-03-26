namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppFunctionInvocationPort
{
    Task<AppFunctionInvokeAcceptedReceipt> InvokeAsync(
        string appId,
        string functionId,
        AppFunctionInvokeRequest request,
        string? releaseId = null,
        CancellationToken ct = default);
}
