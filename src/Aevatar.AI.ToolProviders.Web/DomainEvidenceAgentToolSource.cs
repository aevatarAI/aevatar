using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Effect-free typed evidence proposals intercepted by the NyxIdChat
/// conversation actor. These tools never invoke a provider themselves.
/// </summary>
public sealed class DomainEvidenceAgentToolSource : IAgentToolSource
{
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new ReimbursementEvidenceTool(),
            new CandidateScreeningEvidenceTool(),
        ]);
}
