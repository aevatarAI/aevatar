using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationProjectionPortService : IServiceConfigurationProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceConfigurationRuntimeLease> _activationService;

    public ServiceConfigurationProjectionPortService(
        IProjectionPortActivationService<ServiceConfigurationRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-configuration", string.Empty, actorId, ct);
    }
}
