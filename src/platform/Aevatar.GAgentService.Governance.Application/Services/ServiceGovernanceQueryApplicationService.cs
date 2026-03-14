using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class ServiceGovernanceQueryApplicationService
    : IServiceGovernanceQueryPort
{
    private readonly IServiceBindingQueryReader _bindingQueryReader;
    private readonly IServiceEndpointCatalogQueryReader _endpointCatalogQueryReader;
    private readonly IServicePolicyQueryReader _policyQueryReader;

    public ServiceGovernanceQueryApplicationService(
        IServiceBindingQueryReader bindingQueryReader,
        IServiceEndpointCatalogQueryReader endpointCatalogQueryReader,
        IServicePolicyQueryReader policyQueryReader)
    {
        _bindingQueryReader = bindingQueryReader ?? throw new ArgumentNullException(nameof(bindingQueryReader));
        _endpointCatalogQueryReader = endpointCatalogQueryReader ?? throw new ArgumentNullException(nameof(endpointCatalogQueryReader));
        _policyQueryReader = policyQueryReader ?? throw new ArgumentNullException(nameof(policyQueryReader));
    }

    public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        _bindingQueryReader.GetAsync(identity, ct);

    public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        _endpointCatalogQueryReader.GetAsync(identity, ct);

    public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        _policyQueryReader.GetAsync(identity, ct);
}
