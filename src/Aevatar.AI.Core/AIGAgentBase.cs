// ─────────────────────────────────────────────────────────────
// AIGAgentBase / AIAgentConfig — AI GAgent 基类（组合器）
// 组合 ChatRuntime + ToolManager + ChatHistory + HookPipeline + Middleware
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Hooks.BuiltIn;
using Aevatar.AI.Core.Middleware;
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
    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
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
    public int MaxToolRounds { get; set; } = 40;

    /// <summary>历史消息上限。</summary>
    public int MaxHistoryMessages { get; set; } = 100;

    /// <summary>Prompt token 预算上限。0 = 禁用上下文压缩（默认）。</summary>
    public int MaxPromptTokenBudget { get; set; } = 0;

    /// <summary>触发压缩的阈值比例（0.5~0.99）。当 LastPromptTokens > Budget * Threshold 时触发。</summary>
    public double CompressionThreshold { get; set; } = 0.85;

    /// <summary>是否启用 LLM 摘要压缩（Level 3）。默认关闭。</summary>
    public bool EnableSummarization { get; set; }
}

/// <summary>AI GAgent 基类。组合 ChatRuntime、ToolManager、ChatHistory、HookPipeline、Middleware。</summary>
/// <typeparam name="TState">Agent 状态类型，须为 Protobuf IMessage。</typeparam>
public abstract class AIGAgentBase<TState> : GAgentBase<TState, AIAgentConfig>
    where TState : class, IMessage<TState>, new()
{
    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly IReadOnlyList<IAIGAgentExecutionHook> _additionalHooks;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;

    protected virtual AgentToolApprovalContinuationMode ToolApprovalContinuationMode =>
        AgentToolApprovalContinuationMode.None;

    protected virtual IChatToolCheckpointPort ChatToolCheckpointPort =>
        NoOpChatToolCheckpointPort.Instance;

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
        IAgentToolExecutionPort toolExecutionPort,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _llmProviderFactory = llmProviderFactory ?? NullLLMProviderFactory.Instance;
        _additionalHooks = (additionalHooks ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _toolSources = (toolSources ?? []).ToArray();
    }

    // ─── 初始化 ───

    /// <summary>激活时初始化历史上限并重建 Runtime。</summary>
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        History.MaxMessages = EffectiveConfig.MaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    /// <summary>配置变更时更新历史上限并重建 Runtime。</summary>
    protected override async Task OnEffectiveConfigChangedAsync(AIAgentConfig config, CancellationToken ct)
    {
        History.MaxMessages = config.MaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    protected sealed class AIAgentConfigStateOverrides
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
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

        public bool HasMaxPromptTokenBudget { get; init; }
        public int? MaxPromptTokenBudget { get; init; }

        public bool HasCompressionThreshold { get; init; }
        public double? CompressionThreshold { get; init; }

        public bool HasEnableSummarization { get; init; }
        public bool? EnableSummarization { get; init; }
    }

    /// <summary>Extracts config overrides from protobuf state.</summary>
    protected abstract AIAgentConfigStateOverrides ExtractStateConfigOverrides(TState state);

    protected sealed override AIAgentConfig MergeEffectiveConfig(AIAgentConfig classDefaults, TState state)
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

        if (overrides.HasMaxPromptTokenBudget)
            merged.MaxPromptTokenBudget = Math.Max(0, overrides.MaxPromptTokenBudget ?? 0);

        if (overrides.HasCompressionThreshold && overrides.CompressionThreshold.HasValue)
            merged.CompressionThreshold = overrides.CompressionThreshold.Value;

        if (overrides.HasEnableSummarization && overrides.EnableSummarization.HasValue)
            merged.EnableSummarization = overrides.EnableSummarization.Value;

        NormalizeEffectiveConfig(merged);
        return merged;
    }

    // Refactor (iter15/cluster-024):
    //   Old pattern: protected ChatAsync helpers let GAgents call the non-streaming executor as a formal conversation path.
    //   New principle: GAgent subclasses use ChatStreamAsync; explicit offline aggregation is local to the caller that needs text.
    /// <summary>流式 Chat。</summary>
    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        AgentProfileTurnCatalog? turnCatalog,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatStreamAsync(
            [ContentPart.TextPart(userMessage)],
            EffectiveConfig.MaxToolRounds,
            turnCatalog,
            ct);
    }

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata。</summary>
    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        AgentProfileTurnCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        var maxRounds = ResolveMaxToolRounds(metadata);
        return _chat!.ChatStreamAsync(
            [ContentPart.TextPart(userMessage)],
            maxRounds,
            requestId,
            turnCatalog,
            metadata,
            ct);
    }

    /// <summary>流式 Chat（多模态内容），显式传入稳定 request id 和 metadata。</summary>
    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        string? requestId,
        AgentProfileTurnCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        var maxRounds = ResolveMaxToolRounds(metadata);
        return _chat!.ChatStreamAsync(userContent, maxRounds, requestId, turnCatalog, metadata, ct);
    }

    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        string? requestId,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        AgentProfileTurnCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        var maxRounds = llmControl?.MaxToolRoundsOverride
                        ?? toolContext?.Routing.MaxToolRoundsOverride
                        ?? EffectiveConfig.MaxToolRounds;
        return _chat!.ChatStreamAsync(
            userContent,
            maxRounds,
            requestId,
            llmControl,
            toolContext,
            turnCatalog,
            metadata,
            ct);
    }

    protected IAsyncEnumerable<LLMStreamChunk> ContinueChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        IReadOnlyList<ChatMessage> committedToolTranscript,
        string? requestId,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        AgentProfileTurnCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        var maxRounds = llmControl?.MaxToolRoundsOverride
                        ?? toolContext?.Routing.MaxToolRoundsOverride
                        ?? EffectiveConfig.MaxToolRounds;
        return _chat!.ContinueChatStreamAsync(
            userContent,
            committedToolTranscript,
            maxRounds,
            requestId,
            llmControl,
            toolContext,
            turnCatalog,
            metadata,
            ct);
    }

    protected IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        string? requestId,
        AgentToolExecutionContext? toolContext,
        AgentProfileTurnCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureRuntime();
        var maxRounds = toolContext?.Routing.MaxToolRoundsOverride ?? ResolveMaxToolRounds(metadata);
        return _chat!.ChatStreamAsync(
            userContent,
            maxRounds,
            requestId,
            toolContext,
            turnCatalog,
            metadata,
            ct);
    }

    /// <summary>
    /// Resolve maxToolRounds: metadata override > EffectiveConfig > int.MaxValue (no limit).
    /// </summary>
    private int ResolveMaxToolRounds(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata != null
            && metadata.TryGetValue(LLMRequestMetadataKeys.MaxToolRoundsOverride, out var overrideValue)
            && int.TryParse(overrideValue, out var overrideRounds)
            && overrideRounds > 0)
        {
            return overrideRounds;
        }

        return EffectiveConfig.MaxToolRounds;
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
        var toolLoop = new ToolCallLoop(
            Tools,
            _hooks,
            _llmMiddlewares,
            History.Budget,
            _toolExecutionPort,
            ToolApprovalContinuationMode);
        var compressionConfig = new Chat.ContextCompressionConfig(
            MaxPromptTokenBudget: EffectiveConfig.MaxPromptTokenBudget,
            CompressionThreshold: EffectiveConfig.CompressionThreshold,
            EnableSummarization: EffectiveConfig.EnableSummarization);
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
            compressionConfig: compressionConfig,
            toolCheckpointPort: ChatToolCheckpointPort);
    }

    private ILLMProvider GetLLMProvider()
    {
        if (string.IsNullOrWhiteSpace(EffectiveConfig.ProviderName))
            return _llmProviderFactory.GetDefault();

        var configuredProvider = EffectiveConfig.ProviderName.Trim();
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

    /// <summary>
    /// 装饰系统 prompt。子类可覆写以追加动态内容（如技能列表）。
    /// 默认实现直接返回原始 prompt。
    /// </summary>
    protected virtual string DecorateSystemPrompt(
        string basePrompt,
        AgentProfileTurnCatalog? turnCatalog) => basePrompt;

    private LLMRequest BuildRequest(AgentProfileTurnCatalog? turnCatalog) => new()
    {
        Messages = History.BuildMessages(DecorateSystemPrompt(EffectiveConfig.SystemPrompt, turnCatalog)),
        RequestId = null,
        Metadata = null,
        ToolContext = AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = string.IsNullOrWhiteSpace(Id)
                ? new AgentToolExecutionOwner()
                : AgentToolExecutionOwners.Actor(Id),
        },
        Tools = BuildValidTools(),
        Model = EffectiveConfig.Model,
        Temperature = EffectiveConfig.Temperature,
        MaxTokens = EffectiveConfig.MaxTokens,
    };

    private IReadOnlyList<IAgentTool>? BuildValidTools()
    {
        if (!Tools.HasTools) return null;

        var all = Tools.GetAll();
        var valid = all.Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList();

        if (valid.Count < all.Count)
        {
            var invalidCount = all.Count - valid.Count;
            Logger.LogWarning(
                "[{Role}] Filtered {InvalidCount} tool(s) with empty Name. Registered tools: [{ToolNames}]",
                Id ?? "?",
                invalidCount,
                string.Join(", ", all.Select(t => string.IsNullOrWhiteSpace(t.Name) ? $"<EMPTY:{t.GetType().Name}>" : t.Name)));
        }

        return valid.Count > 0 ? valid : null;
    }

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
                ct.ThrowIfCancellationRequested();
                foreach (var tool in tools)
                    discoveredTools[tool.Name] = tool;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                Logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
            }
        }

        ct.ThrowIfCancellationRequested();
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
        MaxPromptTokenBudget = source.MaxPromptTokenBudget,
        CompressionThreshold = source.CompressionThreshold,
        EnableSummarization = source.EnableSummarization,
    };

    private static void NormalizeEffectiveConfig(AIAgentConfig config)
    {
        config.ProviderName = config.ProviderName?.Trim() ?? string.Empty;
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model.Trim();
        config.SystemPrompt ??= string.Empty;
        if (config.MaxToolRounds <= 0)
            config.MaxToolRounds = 40;
        if (config.MaxHistoryMessages <= 0)
            config.MaxHistoryMessages = 100;
        if (config.MaxPromptTokenBudget < 0)
            config.MaxPromptTokenBudget = 0;
        config.CompressionThreshold = Math.Clamp(config.CompressionThreshold, 0.5, 0.99);
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
