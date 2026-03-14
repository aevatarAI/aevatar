using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRevisionCatalogProjectionPortService : IServiceRevisionCatalogProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceRevisionCatalogRuntimeLease> _activationService;

    public ServiceRevisionCatalogProjectionPortService(
        IProjectionPortActivationService<ServiceRevisionCatalogRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(
            actorId,
            "service-revisions",
            string.Empty,
            actorId,
            ct);
    }
}
