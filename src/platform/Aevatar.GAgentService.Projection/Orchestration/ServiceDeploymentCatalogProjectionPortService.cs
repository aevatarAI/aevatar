using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceDeploymentCatalogProjectionPortService : IServiceDeploymentCatalogProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceDeploymentCatalogRuntimeLease> _activationService;

    public ServiceDeploymentCatalogProjectionPortService(
        IProjectionPortActivationService<ServiceDeploymentCatalogRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-deployments", string.Empty, actorId, ct);
    }
}
