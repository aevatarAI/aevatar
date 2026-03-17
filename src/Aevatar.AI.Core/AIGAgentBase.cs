// ─────────────────────────────────────────────────────────────
// AIGAgentBase — AI GAgent 基类（组合器）
// 组合 ChatRuntime + ToolManager + ChatHistory + HookPipeline
// Config 由子类通过 State 子消息持有，走 Protobuf 统一持久化
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Hooks.BuiltIn;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Core;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core;

/// <summary>AI GAgent 基类。组合 ChatRuntime、ToolManager、ChatHistory、HookPipeline。</summary>
/// <typeparam name="TState">Agent 状态类型，须为 Protobuf IMessage，内含 AIAgentConfigProto 子消息。</typeparam>
public abstract class AIGAgentBase<TState> : GAgentBase<TState>
    where TState : class, IMessage<TState>, new()
{
    private const int DefaultMaxToolRounds = 10;
    private const int DefaultMaxHistoryMessages = 100;

    // ─── Config 抽象契约 ───

    /// <summary>从 State 中提取 config 子消息。</summary>
    protected abstract AIAgentConfigProto GetConfigFromState();

    /// <summary>将 config 子消息写入 State。</summary>
    protected abstract void SetConfigToState(AIAgentConfigProto config);

    /// <summary>当前 config（只读快捷属性）。</summary>
    public AIAgentConfigProto Config => GetConfigFromState();

    /// <summary>更新 config 并触发变更回调。</summary>
    public async Task ConfigureAsync(AIAgentConfigProto config, CancellationToken ct = default)
    {
        SetConfigToState(config);
        await OnConfigChangedAsync(config, ct);
    }

    /// <summary>配置变更回调。默认重建 Runtime。子类可覆盖。</summary>
    protected virtual async Task OnConfigChangedAsync(AIAgentConfigProto config, CancellationToken ct)
    {
        History.MaxMessages = EffectiveMaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    // ─── 组合的组件（各做一件事） ───
    /// <summary>工具管理器。</summary>
    protected ToolManager Tools { get; } = new();

    /// <summary>对话历史。</summary>
    protected ChatHistory History { get; } = new();

    private AgentHookPipeline? _hooks;
    private ChatRuntime? _chat;

    // ─── 初始化 ───

    private int EffectiveMaxHistoryMessages =>
        Config.MaxHistoryMessages > 0 ? Config.MaxHistoryMessages : DefaultMaxHistoryMessages;

    private int EffectiveMaxToolRounds =>
        Config.MaxToolRounds > 0 ? Config.MaxToolRounds : DefaultMaxToolRounds;

    /// <summary>激活时初始化历史上限并重建 Runtime。</summary>
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        History.MaxMessages = EffectiveMaxHistoryMessages;
        await RegisterToolsFromSourcesAsync(ct);
        RebuildRuntime();
    }

    // ─── Chat 快捷方法 ───

    /// <summary>单轮 Chat（含 Tool Calling 循环）。</summary>
    protected Task<string?> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatAsync(userMessage, EffectiveMaxToolRounds, ct);
    }

    /// <summary>流式 Chat。</summary>
    protected IAsyncEnumerable<string> ChatStreamAsync(string userMessage, CancellationToken ct = default)
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
        var hooks = new List<IAIGAgentExecutionHook>
        {
            new ExecutionTraceHook(Logger),
            new ToolTruncationHook(),
            new BudgetMonitorHook(Logger),
        };
        var additional = Services.GetServices<IAIGAgentExecutionHook>();
        hooks.AddRange(additional);
        _hooks = new AgentHookPipeline(hooks, Logger);

        foreach (var hook in hooks)
            RegisterHook(hook);

        var toolLoop = new ToolCallLoop(Tools, _hooks);
        _chat = new ChatRuntime(
            providerFactory: GetLLMProvider,
            history: History,
            toolLoop: toolLoop,
            hooks: _hooks,
            requestBuilder: BuildRequest);
    }

    private ILLMProvider GetLLMProvider()
    {
        var factory = Services.GetRequiredService<ILLMProviderFactory>();
        var name = Config.ProviderName;
        return string.IsNullOrEmpty(name) ? factory.GetDefault() : factory.GetProvider(name);
    }

    private LLMRequest BuildRequest()
    {
        var cfg = Config;
        return new LLMRequest
        {
            Messages = History.BuildMessages(cfg.SystemPrompt),
            Tools = Tools.HasTools ? Tools.GetAll() : null,
            Model = string.IsNullOrEmpty(cfg.Model) ? null : cfg.Model,
            Temperature = cfg.Temperature != 0 ? cfg.Temperature : null,
            MaxTokens = cfg.MaxTokens != 0 ? cfg.MaxTokens : null,
        };
    }

    private void EnsureRuntime()
    {
        if (_chat == null) RebuildRuntime();
    }

    private async Task RegisterToolsFromSourcesAsync(CancellationToken ct)
    {
        var sources = Services.GetServices<IAgentToolSource>().ToList();
        if (sources.Count == 0) return;

        foreach (var source in sources)
        {
            try
            {
                var tools = await source.DiscoverToolsAsync(ct);
                if (tools.Count > 0)
                    Tools.Register(tools);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
            }
        }
    }
}
