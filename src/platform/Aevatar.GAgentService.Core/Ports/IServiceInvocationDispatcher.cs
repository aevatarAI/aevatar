using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core.Ports;

public interface IServiceInvocationDispatcher
{
    Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct = default);
}
