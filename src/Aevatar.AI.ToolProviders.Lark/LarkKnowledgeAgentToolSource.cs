using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkKnowledgeAgentToolSource(
    LarkToolOptions options,
    NyxIdToolOptions nyxOptions,
    ILarkKnowledgeClient client,
    ILogger<LarkKnowledgeAgentToolSource>? logger = null) : IAgentToolSource
{
    private readonly ILogger _logger = logger ?? NullLogger<LarkKnowledgeAgentToolSource>.Instance;

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (!options.EnableDocsSearch)
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        if (string.IsNullOrWhiteSpace(nyxOptions.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping Lark Docs search tool");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        if (string.IsNullOrWhiteSpace(options.ProviderSlug))
        {
            _logger.LogDebug("Lark provider slug not configured, skipping Lark Docs search tool");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>([new LarkDocsSearchTool(client)]);
    }
}
