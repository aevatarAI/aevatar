namespace Aevatar.GAgentService.Core.Ports;

public interface IServiceRuntimeActivator
{
    Task<ServiceRuntimeActivationResult> ActivateAsync(
        ServiceRuntimeActivationRequest request,
        CancellationToken ct = default);

    Task DeactivateAsync(
        ServiceRuntimeDeactivationRequest request,
        CancellationToken ct = default);
}
