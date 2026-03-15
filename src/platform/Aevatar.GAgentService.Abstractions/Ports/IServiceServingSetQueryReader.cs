using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceServingSetQueryReader
{
    Task<ServiceServingSetSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
