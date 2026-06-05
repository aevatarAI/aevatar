using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceInvocationCatalogQueryReader
{
    Task<ServiceInvocationCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
