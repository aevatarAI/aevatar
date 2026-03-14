using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Queries;

public sealed class ServicePolicyQueryReader : IServicePolicyQueryReader
{
    private readonly IProjectionDocumentReader<ServicePolicyCatalogReadModel, string> _documentStore;

    public ServicePolicyQueryReader(IProjectionDocumentReader<ServicePolicyCatalogReadModel, string> documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
    }

    public async Task<ServicePolicyCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServicePolicyCatalogSnapshot(
            readModel.Id,
            readModel.Policies
                .OrderBy(x => x.PolicyId, StringComparer.Ordinal)
                .Select(x => new ServicePolicySnapshot(
                    x.PolicyId,
                    x.DisplayName,
                    [.. x.ActivationRequiredBindingIds],
                    [.. x.InvokeAllowedCallerServiceKeys],
                    x.InvokeRequiresActiveDeployment,
                    x.Retired))
                .ToList(),
            readModel.UpdatedAt);
    }
}
