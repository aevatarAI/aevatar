using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>Exposes only the admitted NyxID proxy surface to durable workflows.</summary>
public sealed class NyxIdWorkflowAgentToolSource(NyxIdAgentToolSource inner) : IAgentToolSource
{
    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        (await inner.DiscoverToolsAsync(ct).ConfigureAwait(false))
        .Where(static tool => string.Equals(tool.Name, "nyxid_proxy", StringComparison.Ordinal))
        .ToArray();
}
