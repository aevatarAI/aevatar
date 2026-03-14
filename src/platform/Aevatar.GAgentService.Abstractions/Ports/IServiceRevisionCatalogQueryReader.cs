using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRevisionCatalogQueryReader
{
    Task<ServiceRevisionCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
