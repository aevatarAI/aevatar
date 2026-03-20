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
    public DefaultServiceGovernanceCommandTargetProvisioner(IActorRuntime runtime)
        : base(runtime)
    {
    }

    public async Task<string> EnsureConfigurationTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return await EnsureActorAsync<ServiceConfigurationGAgent>(ServiceActorIds.Configuration(identity), ct);
    }
}
