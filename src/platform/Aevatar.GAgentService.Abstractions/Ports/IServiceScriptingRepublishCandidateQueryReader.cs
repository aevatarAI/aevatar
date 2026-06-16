using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceScriptingRepublishCandidateQueryReader
{
    Task<IReadOnlyList<ServiceScriptingRepublishCandidateSnapshot>> QueryServingByScopeScriptAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default);
}
