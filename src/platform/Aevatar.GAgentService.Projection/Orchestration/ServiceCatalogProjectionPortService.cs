using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceCatalogProjectionPortService : IServiceCatalogProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceCatalogRuntimeLease> _activationService;

    public ServiceCatalogProjectionPortService(
        IProjectionPortActivationService<ServiceCatalogRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(
            actorId,
            "service-catalog",
            string.Empty,
            actorId,
            ct);
    }
}
