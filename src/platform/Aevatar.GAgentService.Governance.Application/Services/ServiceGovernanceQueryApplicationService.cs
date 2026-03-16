using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class ServiceGovernanceQueryApplicationService
    : IServiceGovernanceQueryPort
{
    private readonly IServiceConfigurationQueryReader _configurationQueryReader;

    public ServiceGovernanceQueryApplicationService(
        IServiceConfigurationQueryReader configurationQueryReader)
    {
        _configurationQueryReader = configurationQueryReader ?? throw new ArgumentNullException(nameof(configurationQueryReader));
    }

    public async Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var configuration = await _configurationQueryReader.GetAsync(identity, ct);
        return configuration == null
            ? null
            : new ServiceBindingCatalogSnapshot(configuration.ServiceKey, configuration.Bindings, configuration.UpdatedAt);
    }

    public async Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var configuration = await _configurationQueryReader.GetAsync(identity, ct);
        return configuration == null
            ? null
            : new ServiceEndpointCatalogSnapshot(configuration.ServiceKey, configuration.Endpoints, configuration.UpdatedAt);
    }

    public async Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var configuration = await _configurationQueryReader.GetAsync(identity, ct);
        return configuration == null
            ? null
            : new ServicePolicyCatalogSnapshot(configuration.ServiceKey, configuration.Policies, configuration.UpdatedAt);
    }
}
