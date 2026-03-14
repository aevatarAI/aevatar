using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceGovernanceQueryPort
{
    Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
