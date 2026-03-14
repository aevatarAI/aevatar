using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceBindingProjectionPortService : IServiceBindingProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceBindingRuntimeLease> _activationService;

    public ServiceBindingProjectionPortService(
        IProjectionPortActivationService<ServiceBindingRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-bindings", string.Empty, actorId, ct);
    }
}
