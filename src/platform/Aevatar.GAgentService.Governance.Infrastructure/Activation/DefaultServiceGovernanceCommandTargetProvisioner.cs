using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Activation;

namespace Aevatar.GAgentService.Governance.Infrastructure.Activation;

public sealed class DefaultServiceGovernanceCommandTargetProvisioner
    : ActorTargetProvisionerBase, IServiceGovernanceCommandTargetProvisioner
{
#pragma warning disable CS0618 // Legacy migration — remove after all deployments complete governance migration
    private readonly IServiceGovernanceLegacyImporter _legacyImporter;

    public DefaultServiceGovernanceCommandTargetProvisioner(
        IActorRuntime runtime,
        IServiceGovernanceLegacyImporter legacyImporter)
        : base(runtime)
    {
        _legacyImporter = legacyImporter ?? throw new ArgumentNullException(nameof(legacyImporter));
    }
#pragma warning restore CS0618

    public async Task<string> EnsureConfigurationTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _ = await _legacyImporter.ImportIfNeededAsync(identity, ct);
        return await EnsureActorAsync<ServiceConfigurationGAgent>(ServiceActorIds.Configuration(identity), ct);
    }
}
