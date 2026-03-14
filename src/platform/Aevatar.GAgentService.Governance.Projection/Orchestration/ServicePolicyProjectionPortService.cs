using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServicePolicyProjectionPortService : IServicePolicyProjectionPort
{
    private readonly IProjectionPortActivationService<ServicePolicyRuntimeLease> _activationService;

    public ServicePolicyProjectionPortService(
        IProjectionPortActivationService<ServicePolicyRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, "service-policies", string.Empty, actorId, ct);
    }
}
