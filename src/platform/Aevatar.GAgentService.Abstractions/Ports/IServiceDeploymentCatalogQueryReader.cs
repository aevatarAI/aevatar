using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceDeploymentCatalogQueryReader
{
    Task<ServiceDeploymentCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
