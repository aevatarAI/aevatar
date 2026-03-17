using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Configuration;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationProjectionPort
    : MaterializationProjectionPortBase<ServiceConfigurationRuntimeLease>,
      IServiceConfigurationProjectionPort
{
    public ServiceConfigurationProjectionPort(
        ServiceGovernanceProjectionOptions options,
        IProjectionMaterializationActivationService<ServiceConfigurationRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<ServiceConfigurationRuntimeLease> releaseService)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ServiceGovernanceProjectionKinds.Configuration,
            },
            ct);
    }
}
