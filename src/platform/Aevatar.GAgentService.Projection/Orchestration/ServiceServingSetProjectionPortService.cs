using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceServingSetProjectionPortService : IServiceServingSetProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceServingSetRuntimeLease> _activationService;

    public ServiceServingSetProjectionPortService(
        IProjectionPortActivationService<ServiceServingSetRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-serving", string.Empty, actorId, ct);
    }
}
