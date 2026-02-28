using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultServiceModePolicyPort : IServiceModePolicyPort
{
    public Task<ServiceModeDecision> ValidateAsync(ServiceModePolicyRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.StackId) || string.IsNullOrWhiteSpace(request.ServiceName))
            return Task.FromResult(new ServiceModeDecision(false, "SERVICE_MODE_CONFLICT"));
        if (request.ReplicasDesired < 0)
            return Task.FromResult(new ServiceModeDecision(false, "SERVICE_MODE_CONFLICT"));
        if ((request.ServiceMode is DynamicServiceMode.Daemon or DynamicServiceMode.Hybrid) && request.ReplicasDesired < 1)
            return Task.FromResult(new ServiceModeDecision(false, "SERVICE_MODE_CONFLICT"));

        return Task.FromResult(new ServiceModeDecision(true));
    }
}
