using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceConfigurationQueryReader
{
    Task<ServiceConfigurationSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
