using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceEndpointCatalogProjectionPortService : IServiceEndpointCatalogProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceEndpointCatalogRuntimeLease> _activationService;

    public ServiceEndpointCatalogProjectionPortService(
        IProjectionPortActivationService<ServiceEndpointCatalogRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-endpoint-catalog", string.Empty, actorId, ct);
    }
}
