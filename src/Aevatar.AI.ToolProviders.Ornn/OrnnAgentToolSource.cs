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
    private readonly OrnnPublishSkillTool _publishTool;
    private readonly ILogger _logger;

    public OrnnAgentToolSource(
        OrnnOptions options,
        OrnnSkillClient client,
        OrnnPublishSkillTool publishTool,
        ILogger<OrnnAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _publishTool = publishTool;
        _logger = logger ?? NullLogger<OrnnAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        // ornn_search_skills must always be advertised to the LLM regardless of how the
        // deployment configured the Ornn slug, otherwise the model loses the typed entry
        // point and resorts to nyxid_proxy path-guessing (issue #530). OrnnSkillClient
        // routes through NyxID's proxy, so the slug — not a hardcoded base URL — is what
        // determines reachability.
        IReadOnlyList<IAgentTool> tools = [new OrnnSearchSkillsTool(_client), _publishTool];

        _logger.LogInformation(
            "Ornn tools registered (NyxID slug: {Slug})", _options.NyxIdSlug);
        return Task.FromResult(tools);
    }
}
