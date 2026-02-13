// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 从 AIGAgentBase 拆出。组合 LLMProvider + History + ToolCallLoop + Hooks。

using Aevatar.AI.Hooks;
using Aevatar.AI.LLM;
using Aevatar.AI.Tools;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aevatar.AI.Chat;

/// <summary>Chat 执行运行时。只做一件事：调 LLM，管理历史。</summary>
public sealed class ChatRuntime
{
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ChatHistory _history;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<LLMRequest> _requestBuilder;

    public ChatRuntime(
        Func<ILLMProvider> providerFactory,
        ChatHistory history,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<LLMRequest> requestBuilder)
    {
        _providerFactory = providerFactory;
        _history = history;
        _toolLoop = toolLoop;
        _hooks = hooks;
        _requestBuilder = requestBuilder;
    }

    /// <summary>单轮 Chat（含 Tool Calling 循环）。</summary>
    public async Task<string?> ChatAsync(string userMessage, int maxToolRounds = 10, CancellationToken ct = default)
    {
        _history.Add(ChatMessage.User(userMessage));
        var baseRequest = _requestBuilder();
        var provider = _providerFactory();
        var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);

        var result = await _toolLoop.ExecuteAsync(provider, messages, baseRequest, maxToolRounds, ct);

        // 同步历史（ToolCallLoop 直接操作 messages 列表）
        _history.Clear();
        foreach (var m in messages.Where(m => m.Role != "system"))
            _history.Add(m);

        return result;
    }

    /// <summary>流式 Chat。</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(ChatMessage.User(userMessage));
        var baseRequest = _requestBuilder();
        var provider = _providerFactory();
        var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);
        var request = new LLMRequest { Messages = messages, Tools = baseRequest.Tools, Model = baseRequest.Model, Temperature = baseRequest.Temperature, MaxTokens = baseRequest.MaxTokens };

        var full = new StringBuilder();
        await foreach (var chunk in provider.ChatStreamAsync(request, ct))
        {
            if (chunk.DeltaContent != null) { full.Append(chunk.DeltaContent); yield return chunk.DeltaContent; }
        }

        if (full.Length > 0) _history.Add(ChatMessage.Assistant(full.ToString()));
    }
}
