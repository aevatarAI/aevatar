using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceGovernanceLegacyImporter
{
    Task<bool> ImportIfNeededAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
