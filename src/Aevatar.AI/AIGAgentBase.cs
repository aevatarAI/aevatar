// ─────────────────────────────────────────────────────────────
// AIGAgentBase / AIAgentConfig — AI GAgent 基类（组合器）
// 组合 ChatRuntime + ToolManager + ChatHistory + HookPipeline
// 不是上帝类——实际逻辑在四个独立组件中
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Chat;
using Aevatar.AI.Hooks;
using Aevatar.AI.Hooks.BuiltIn;
using Aevatar.AI.LLM;
using Aevatar.AI.Tools;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI;

/// <summary>AI Agent 配置。Provider、Model、Prompt、历史与 Tool 轮数等。</summary>
public sealed class AIAgentConfig
{
    /// <summary>LLM Provider 名称。</summary>
    public string ProviderName { get; set; } = "deepseek";

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
}

/// <summary>AI GAgent 基类。组合 ChatRuntime、ToolManager、ChatHistory、HookPipeline。</summary>
/// <typeparam name="TState">Agent 状态类型，须为 Protobuf IMessage。</typeparam>
public abstract class AIGAgentBase<TState> : GAgentBase<TState, AIAgentConfig>
    where TState : class, IMessage<TState>, new()
{
    // ─── 组合的组件（各做一件事） ───
    /// <summary>工具管理器。</summary>
    protected ToolManager Tools { get; } = new();

    /// <summary>对话历史。</summary>
    protected ChatHistory History { get; } = new();

    private AgentHookPipeline? _hooks;
    private ChatRuntime? _chat;

    // ─── 初始化 ───

    /// <summary>激活时初始化历史上限并重建 Runtime。</summary>
    protected override Task OnActivateAsync(CancellationToken ct)
    {
        History.MaxMessages = Config.MaxHistoryMessages;
        RebuildRuntime();
        return Task.CompletedTask;
    }

    /// <summary>配置变更时更新历史上限并重建 Runtime。</summary>
    protected override Task OnConfigChangedAsync(AIAgentConfig config, CancellationToken ct)
    {
        History.MaxMessages = config.MaxHistoryMessages;
        RebuildRuntime();
        return Task.CompletedTask;
    }

    // ─── Chat 快捷方法 ───

    /// <summary>单轮 Chat（含 Tool Calling 循环）。</summary>
    /// <param name="userMessage">用户输入。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>LLM 最终回复文本，或 null。</returns>
    protected Task<string?> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatAsync(userMessage, Config.MaxToolRounds, ct);
    }

    /// <summary>流式 Chat。</summary>
    /// <param name="userMessage">用户输入。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>增量文本的异步序列。</returns>
    protected IAsyncEnumerable<string> ChatStreamAsync(string userMessage, CancellationToken ct = default)
    {
        EnsureRuntime();
        return _chat!.ChatStreamAsync(userMessage, ct);
    }

    /// <summary>注册单个工具。</summary>
    /// <param name="tool">要注册的工具。</param>
    protected void RegisterTool(IAgentTool tool) => Tools.Register(tool);

    /// <summary>清空对话历史。</summary>
    protected void ClearHistory() => History.Clear();

    // ─── Hook 双通道说明 ───
    // 1. Foundation 级（Event Handler hooks）：由 GAgentBase._hooks pipeline 驱动
    //    → RebuildRuntime 中通过 RegisterHook() 注册 built-in IAgentHook 到 Foundation pipeline
    //    → IAgentHook : IGAgentHook，所以 Foundation 可以调用 OnEventHandlerStart/End/OnError
    // 2. AI 级（LLM / Tool hooks）：由 AIGAgentBase._hooks (AgentHookPipeline) 驱动
    //    → ChatRuntime / ToolCallLoop 在 LLM/Tool 前后调用 AI pipeline

    // ─── 内部构建 ───

    private void RebuildRuntime()
    {
        // 构建 AI Hook Pipeline（内置 + DI 注入）
        var hooks = new List<IAgentHook>
        {
            new ExecutionTraceHook(Logger),
            new ToolTruncationHook(),
            new BudgetMonitorHook(Logger),
        };
        var additional = Services.GetServices<IAgentHook>();
        hooks.AddRange(additional);
        _hooks = new AgentHookPipeline(hooks, Logger);

        // 注册 AI hooks 到 Foundation 的 IGAgentHook pipeline
        // IAgentHook : IGAgentHook，所以 Foundation 层能调用 Event Handler hooks
        foreach (var hook in hooks)
            RegisterHook(hook);

        // 构建 Chat Runtime
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
        return string.IsNullOrEmpty(Config.ProviderName) ? factory.GetDefault() : factory.GetProvider(Config.ProviderName);
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
}
