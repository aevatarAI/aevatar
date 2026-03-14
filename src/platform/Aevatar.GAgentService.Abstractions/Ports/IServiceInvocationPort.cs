namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceInvocationPort
{
    Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default);
}
