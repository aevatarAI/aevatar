using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationProjectionPort
    : MaterializationProjectionPortBase<ServiceConfigurationRuntimeLease>,
      IServiceConfigurationProjectionPort
{
    public ServiceConfigurationProjectionPort(
        IProjectionMaterializationActivationService<ServiceConfigurationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = "service-configuration",
            },
            ct);
    }
}
