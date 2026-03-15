using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRolloutProjectionPortService : IServiceRolloutProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceRolloutRuntimeLease> _activationService;

    public ServiceRolloutProjectionPortService(
        IProjectionPortActivationService<ServiceRolloutRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-rollouts", string.Empty, actorId, ct);
    }
}
