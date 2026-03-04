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
    public int MaxToolRounds { get; set; } = 10;

    /// <summary>历史消息上限。</summary>
    public int MaxHistoryMessages { get; set; } = 100;

    /// <summary>流式输出缓冲区容量（用于背压控制）。</summary>
    public int StreamBufferCapacity { get; set; } = 256;

    /// <summary>业务应用配置 JSON（由业务层自行解释）。</summary>
    public string AppConfigJson { get; set; } = string.Empty;

    /// <summary>业务应用配置编解码标识（例如 json/plain）。</summary>
    public string AppConfigCodec { get; set; } = string.Empty;

    /// <summary>业务应用配置 schema 版本。</summary>
    public int AppConfigSchemaVersion { get; set; }
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

    /// <summary>
    /// State-derived config overrides with explicit presence bits.
    /// </summary>
    protected sealed class AIAgentConfigStateOverrides
    {
        public bool HasProviderName { get; init; }
        public string? ProviderName { get; init; }

        public bool HasModel { get; init; }
        public string? Model { get; init; }

        public bool HasSystemPrompt { get; init; }
        public string? SystemPrompt { get; init; }

        public bool HasTemperature { get; init; }
        public double? Temperature { get; init; }

        public bool HasMaxTokens { get; init; }
        public int? MaxTokens { get; init; }

        public bool HasMaxToolRounds { get; init; }
        public int? MaxToolRounds { get; init; }

        public bool HasMaxHistoryMessages { get; init; }
        public int? MaxHistoryMessages { get; init; }

        public bool HasStreamBufferCapacity { get; init; }
        public int? StreamBufferCapacity { get; init; }

        public bool HasAppConfigJson { get; init; }
        public string? AppConfigJson { get; init; }

        public bool HasAppConfigCodec { get; init; }
        public string? AppConfigCodec { get; init; }

        public bool HasAppConfigSchemaVersion { get; init; }
        public int? AppConfigSchemaVersion { get; init; }
    }

    /// <summary>Extracts config overrides from protobuf state.</summary>
    protected abstract AIAgentConfigStateOverrides ExtractStateConfigOverrides(TState state);

    protected sealed override AIAgentConfig MergeConfig(AIAgentConfig classDefaults, TState state)
    {
        ArgumentNullException.ThrowIfNull(classDefaults);
        ArgumentNullException.ThrowIfNull(state);

        var merged = CloneConfig(classDefaults);
        var overrides = ExtractStateConfigOverrides(state);
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.HasProviderName)
            merged.ProviderName = (overrides.ProviderName ?? string.Empty).Trim();

        if (overrides.HasModel)
            merged.Model = string.IsNullOrWhiteSpace(overrides.Model) ? null : overrides.Model.Trim();

        if (overrides.HasSystemPrompt)
            merged.SystemPrompt = overrides.SystemPrompt ?? string.Empty;

        if (overrides.HasTemperature)
            merged.Temperature = overrides.Temperature;

        if (overrides.HasMaxTokens)
            merged.MaxTokens = (overrides.MaxTokens ?? 0) > 0 ? overrides.MaxTokens : null;

        if (overrides.HasMaxToolRounds && (overrides.MaxToolRounds ?? 0) > 0)
            merged.MaxToolRounds = overrides.MaxToolRounds!.Value;

        if (overrides.HasMaxHistoryMessages && (overrides.MaxHistoryMessages ?? 0) > 0)
            merged.MaxHistoryMessages = overrides.MaxHistoryMessages!.Value;

        if (overrides.HasStreamBufferCapacity && (overrides.StreamBufferCapacity ?? 0) > 0)
            merged.StreamBufferCapacity = overrides.StreamBufferCapacity!.Value;

        if (overrides.HasAppConfigJson)
            merged.AppConfigJson = overrides.AppConfigJson ?? string.Empty;

        if (overrides.HasAppConfigCodec)
            merged.AppConfigCodec = overrides.AppConfigCodec ?? string.Empty;

        if (overrides.HasAppConfigSchemaVersion)
            merged.AppConfigSchemaVersion = overrides.AppConfigSchemaVersion ?? 0;

        NormalizeEffectiveConfig(merged);
        return merged;
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
        if (string.IsNullOrWhiteSpace(Config.ProviderName))
            return _llmProviderFactory.GetDefault();

        var configuredProvider = Config.ProviderName.Trim();
        var availableProviders = _llmProviderFactory.GetAvailableProviders();
        if (availableProviders.Any(name => string.Equals(name, configuredProvider, StringComparison.OrdinalIgnoreCase)))
            return _llmProviderFactory.GetProvider(configuredProvider);

        Logger.LogWarning(
            "Configured provider '{ConfiguredProvider}' is unavailable for {AgentType}({AgentId}); fallback to default provider. Available providers: {AvailableProviders}",
            configuredProvider,
            GetType().Name,
            Id,
            availableProviders.Count > 0 ? string.Join(", ", availableProviders) : "<none>");

        return _llmProviderFactory.GetDefault();
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

    private static AIAgentConfig CloneConfig(AIAgentConfig source) => new()
    {
        ProviderName = source.ProviderName ?? string.Empty,
        Model = source.Model,
        SystemPrompt = source.SystemPrompt ?? string.Empty,
        Temperature = source.Temperature,
        MaxTokens = source.MaxTokens,
        MaxToolRounds = source.MaxToolRounds,
        MaxHistoryMessages = source.MaxHistoryMessages,
        StreamBufferCapacity = source.StreamBufferCapacity,
        AppConfigJson = source.AppConfigJson ?? string.Empty,
        AppConfigCodec = source.AppConfigCodec ?? string.Empty,
        AppConfigSchemaVersion = source.AppConfigSchemaVersion,
    };

    private static void NormalizeEffectiveConfig(AIAgentConfig config)
    {
        config.ProviderName = config.ProviderName?.Trim() ?? string.Empty;
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model.Trim();
        config.SystemPrompt ??= string.Empty;
        if (config.MaxToolRounds <= 0)
            config.MaxToolRounds = 10;
        if (config.MaxHistoryMessages <= 0)
            config.MaxHistoryMessages = 100;
        if (config.StreamBufferCapacity <= 0)
            config.StreamBufferCapacity = 256;
        config.AppConfigJson ??= string.Empty;
        config.AppConfigCodec ??= string.Empty;
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
