// ─────────────────────────────────────────────────────────────
// AIGAgentBase / AIAgentConfig — AI GAgent 基类（组合器）
// 组合 ChatRuntime + ToolManager + ChatHistory + HookPipeline + Middleware
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Hooks.BuiltIn;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core;

/// <summary>AI Agent 配置。Provider、Model、Prompt、历史与 Tool 轮数等。</summary>
public sealed class AIAgentConfig
{
    /// <summary>LLM Provider 名称。</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>模型名称，可选，覆盖 Provider 默认。</summary>
    public string? Model { get; set; }

    /// <summary>System Prompt。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>温度参数。</summary>
    public double? Temperature { get; set; }

    /// <summary>最大生成 Token 数。</summary>
    public int? MaxTokens { get; set; }

    /// <summary>单轮 Chat 最大 Tool Calling 轮数。</summary>
    public int MaxToolRounds { get; set; } = 30;

    /// <summary>历史消息上限。</summary>
    public int MaxHistoryMessages { get; set; } = 100;

    /// <summary>流式输出缓冲区容量（用于背压控制）。</summary>
    public int StreamBufferCapacity { get; set; } = 256;
}

/// <summary>AI GAgent 基类。组合 ChatRuntime、ToolManager、ChatHistory、HookPipeline、Middleware。</summary>
/// <typeparam name="TState">Agent 状态类型，须为 Protobuf IMessage。</typeparam>
public abstract class AIGAgentBase<TState> : GAgentBase<TState, AIAgentConfig>
    where TState : class, IMessage<TState>, new()
{
    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IReadOnlyList<IAIGAgentExecutionHook> _additionalHooks;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;

    // ─── 组合的组件（各做一件事） ───
    /// <summary>工具管理器。</summary>
    protected ToolManager Tools { get; } = new();

    /// <summary>对话历史。</summary>
    protected ChatHistory History { get; } = new();

    // Track source-loaded tools so reconfiguration can remove stale entries.
    private readonly HashSet<string> _sourceToolNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _foundationHooksRegistered;
    private AgentHookPipeline? _hooks;
    private ChatRuntime? _chat;

    protected AIGAgentBase(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
    {
        _llmProviderFactory = llmProviderFactory ?? NullLLMProviderFactory.Instance;
        _additionalHooks = (additionalHooks ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _toolMiddlewares = (toolMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _toolSources = (toolSources ?? []).ToArray();
    }

    // ─── 初始化 ───

    /// <summary>激活时初始化历史上限并重建 Runtime。</summary>
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        History.MaxMessages = Config.MaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    /// <summary>配置变更时更新历史上限并重建 Runtime。</summary>
    protected override async Task OnConfigChangedAsync(AIAgentConfig config, CancellationToken ct)
    {
        History.MaxMessages = config.MaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    // ─── Chat 快捷方法 ───

    /// <summary>单轮 Chat（含 Tool Calling 循环）。</summary>
    protected Task<string?> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatAsync(userMessage, Config.MaxToolRounds, ct);
    }

    /// <summary>流式 Chat。</summary>
    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(string userMessage, CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatStreamAsync(userMessage, ct);
    }

    /// <summary>注册单个工具。</summary>
    protected void RegisterTool(IAgentTool tool) => Tools.Register(tool);

    /// <summary>清空对话历史。</summary>
    protected void ClearHistory() => History.Clear();

    // ─── 内部构建 ───

    private void RebuildRuntime()
    {
        // 构建 AI Hook Pipeline（内置 + 外部注入）
        var hooks = new List<IAIGAgentExecutionHook>
        {
            new ExecutionTraceHook(Logger),
            new ToolTruncationHook(),
            new BudgetMonitorHook(Logger),
        };
        hooks.AddRange(_additionalHooks);
        _hooks = new AgentHookPipeline(hooks, Logger);

        if (!_foundationHooksRegistered)
        {
            foreach (var hook in hooks)
                RegisterHook(hook);

            _foundationHooksRegistered = true;
        }

        // 构建 Chat Runtime
        var toolLoop = new ToolCallLoop(Tools, _hooks, _toolMiddlewares, _llmMiddlewares);
        _chat = new ChatRuntime(
            providerFactory: GetLLMProvider,
            history: History,
            toolLoop: toolLoop,
            hooks: _hooks,
            requestBuilder: BuildRequest,
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: Id,
            agentName: GetType().Name,
            streamBufferCapacity: Config.StreamBufferCapacity);
    }

    private ILLMProvider GetLLMProvider()
    {
        return string.IsNullOrEmpty(Config.ProviderName)
            ? _llmProviderFactory.GetDefault()
            : _llmProviderFactory.GetProvider(Config.ProviderName);
    }

    private LLMRequest BuildRequest() => new()
    {
        Messages = History.BuildMessages(Config.SystemPrompt),
        Tools = Tools.HasTools ? Tools.GetAll() : null,
        Model = Config.Model,
        Temperature = Config.Temperature,
        MaxTokens = Config.MaxTokens,
    };

    private void EnsureRuntime()
    {
        if (_chat == null) RebuildRuntime();
    }

    private async Task RegisterToolsFromSourcesAsync(CancellationToken ct)
    {
        if (_toolSources.Count == 0)
        {
            RefreshSourceTools([]);
            return;
        }

        var discoveredTools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _toolSources)
        {
            try
            {
                var tools = await source.DiscoverToolsAsync(ct);
                foreach (var tool in tools)
                    discoveredTools[tool.Name] = tool;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
            }
        }

        RefreshSourceTools(discoveredTools.Values);
    }

    private void RefreshSourceTools(IEnumerable<IAgentTool> discoveredTools)
    {
        foreach (var toolName in _sourceToolNames)
            Tools.Unregister(toolName);

        _sourceToolNames.Clear();

        foreach (var tool in discoveredTools)
        {
            Tools.Register(tool);
            _sourceToolNames.Add(tool.Name);
        }
    }

    private sealed class NullLLMProviderFactory : ILLMProviderFactory
    {
        public static readonly NullLLMProviderFactory Instance = new();

        public ILLMProvider GetProvider(string name) =>
            throw new InvalidOperationException($"LLM provider factory is not configured. Cannot resolve provider '{name}'.");

        public ILLMProvider GetDefault() =>
            throw new InvalidOperationException("LLM provider factory is not configured. Cannot resolve default provider.");

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }
}
