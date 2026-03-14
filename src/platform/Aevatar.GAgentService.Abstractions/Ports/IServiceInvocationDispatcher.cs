using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceInvocationDispatcher
{
    Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct = default);
}
