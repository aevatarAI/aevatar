// ─────────────────────────────────────────────────────────────
// SkillsAgentToolSource — 统一技能工具来源
// 扫描本地技能 → 注册到 LocalSkillCatalog → 返回统一 UseSkillTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Skills 工具来源。发现本地技能并提供统一的 use_skill 工具。
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class SkillsAgentToolSource : IAgentToolSource
{
    private readonly SkillsOptions _options;
    private readonly SkillDiscovery _discovery;
    private readonly LocalSkillCatalog _localCatalog;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly IRemoteSkillAccessTokenResolver? _remoteAccessTokenResolver;
    private readonly ISkillWorkflowMountPort _workflowMountPort;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;
    private readonly ILogger _logger;

    public SkillsAgentToolSource(
        SkillsOptions options,
        SkillDiscovery discovery,
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher = null,
        ISkillWorkflowMountPort? workflowMountPort = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null,
        IRemoteSkillAccessTokenResolver? remoteAccessTokenResolver = null,
        ILogger<SkillsAgentToolSource>? logger = null)
    {
        _options = options;
        _discovery = discovery;
        _localCatalog = localCatalog;
        _remoteFetcher = remoteFetcher;
        _remoteAccessTokenResolver = remoteAccessTokenResolver;
        _workflowMountPort = workflowMountPort ?? new NoOpSkillWorkflowMountPort();
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort;
        _logger = logger ?? NullLogger<SkillsAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        // 1. 扫描本地目录 → 注册到 LocalSkillCatalog
        foreach (var directory in _options.Directories)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var skills = _discovery.ScanDirectory(directory);
                _localCatalog.RegisterRange(skills);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skill discovery failed for directory {Directory}", directory);
            }
        }

        // 2. 返回统一的 UseSkillTool（单个工具）
        IReadOnlyList<IAgentTool> tools =
        [
            new UseSkillTool(
                _localCatalog,
                _remoteFetcher,
                workflowMountPort: _workflowMountPort,
                scopeWorkflowCommandPort: _scopeWorkflowCommandPort,
                remoteAccessTokenResolver: _remoteAccessTokenResolver),
        ];
        return Task.FromResult(tools);
    }
}
