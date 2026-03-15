using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceGovernanceCommandTargetProvisioner
{
    Task<string> EnsureConfigurationTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
