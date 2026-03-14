using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceCatalogQueryReader
{
    Task<ServiceCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default);
}
