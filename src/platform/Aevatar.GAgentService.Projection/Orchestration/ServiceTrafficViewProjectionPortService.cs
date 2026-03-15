using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceTrafficViewProjectionPortService : IServiceTrafficViewProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceTrafficViewRuntimeLease> _activationService;

    public ServiceTrafficViewProjectionPortService(
        IProjectionPortActivationService<ServiceTrafficViewRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-traffic", string.Empty, actorId, ct);
    }
}
