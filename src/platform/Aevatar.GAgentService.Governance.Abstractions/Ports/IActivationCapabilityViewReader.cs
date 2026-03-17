using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IActivationCapabilityViewReader
{
    Task<ActivationCapabilityView> GetAsync(
        ServiceIdentity identity,
        string revisionId,
        CancellationToken ct = default);
}
