using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Ornn 技能工具来源。提供 ornn_search_skills 发现工具。
/// 技能使用功能已合入统一的 use_skill 工具（通过 IRemoteSkillFetcher）。
/// </summary>
public sealed class OrnnAgentToolSource : IAgentToolSource
{
    private readonly OrnnOptions _options;
    private readonly OrnnSkillClient _client;
    private readonly ILogger _logger;

    public OrnnAgentToolSource(
        OrnnOptions options,
        OrnnSkillClient client,
        ILogger<OrnnAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<OrnnAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogDebug("Ornn base URL not configured, skipping Ornn skill tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        IReadOnlyList<IAgentTool> tools = [new OrnnSearchSkillsTool(_client)];

        _logger.LogInformation(
            "Ornn search tool registered (base URL: {BaseUrl})", _options.BaseUrl);
        return Task.FromResult(tools);
    }
}
