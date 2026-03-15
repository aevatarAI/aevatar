using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceTrafficViewQueryReader
{
    Task<ServiceTrafficViewSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
