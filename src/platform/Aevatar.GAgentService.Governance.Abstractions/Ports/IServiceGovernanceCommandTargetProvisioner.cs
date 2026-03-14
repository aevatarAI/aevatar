using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceGovernanceCommandTargetProvisioner
{
    Task<string> EnsureBindingCatalogTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsureEndpointCatalogTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsurePolicyCatalogTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
