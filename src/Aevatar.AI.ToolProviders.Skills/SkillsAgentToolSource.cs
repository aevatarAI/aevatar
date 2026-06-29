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
    private readonly IEnumerable<SkillDefinition> _builtInSkills;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly ISkillWorkflowMountPort _workflowMountPort;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;
    private readonly ISkillCapabilityExecutionPort? _capabilityExecutionPort;
    private readonly ILogger _logger;

    public SkillsAgentToolSource(
        SkillsOptions options,
        SkillDiscovery discovery,
        LocalSkillCatalog localCatalog,
        IEnumerable<SkillDefinition>? builtInSkills = null,
        IRemoteSkillFetcher? remoteFetcher = null,
        ISkillWorkflowMountPort? workflowMountPort = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null,
        ISkillCapabilityExecutionPort? capabilityExecutionPort = null,
        ILogger<SkillsAgentToolSource>? logger = null)
    {
        _options = options;
        _discovery = discovery;
        _localCatalog = localCatalog;
        _builtInSkills = builtInSkills ?? [];
        _remoteFetcher = remoteFetcher;
        _workflowMountPort = workflowMountPort ?? new NoOpSkillWorkflowMountPort();
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort;
        _capabilityExecutionPort = capabilityExecutionPort;
        _logger = logger ?? NullLogger<SkillsAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        _localCatalog.RegisterRange(_builtInSkills);

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
        var tools = new List<IAgentTool>
        {
            new UseSkillTool(
                _localCatalog,
                _remoteFetcher,
                workflowMountPort: _workflowMountPort,
                scopeWorkflowCommandPort: _scopeWorkflowCommandPort),
        };
        if (_capabilityExecutionPort is not null)
        {
            tools.AddRange(_localCatalog
                .GetCapabilityProviders()
                .SelectMany(skill => skill.Capabilities
                    .Where(static capability => !string.IsNullOrWhiteSpace(capability.Capability))
                    .Select(capability => new SkillCapabilityTool(skill, capability, _capabilityExecutionPort))));
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private sealed class SkillCapabilityTool(
        SkillDefinition skill,
        SkillCapabilityDescriptor capability,
        ISkillCapabilityExecutionPort executionPort)
        : IAgentTool, IAgentToolCapabilityDescriptor
    {
        public string Name => capability.ToolName;

        public string Description => string.IsNullOrWhiteSpace(capability.Description)
            ? $"Skill capability: {capability.Capability}."
            : capability.Description;

        public string ParametersSchema => string.IsNullOrWhiteSpace(capability.ParametersSchema)
            ? "{\"type\":\"object\"}"
            : capability.ParametersSchema;

        public IReadOnlyCollection<string> Capabilities { get; } =
        [
            capability.Capability,
            AgentToolCapabilities.ExcludeFromDirectChannelChat,
        ];

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            executionPort.ExecuteAsync(
                new SkillCapabilityExecutionRequest
                {
                    Skill = skill,
                    Capability = capability,
                    ArgumentsJson = argumentsJson,
                },
                ct);
    }
}
