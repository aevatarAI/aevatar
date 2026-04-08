// ─────────────────────────────────────────────────────────────
// ChatHistory — 对话历史管理
// 追加、截断、压缩。从 AIGAgentBase 拆出。
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Chat;

/// <summary>对话历史管理器。维护消息列表，支持按上限截断。</summary>
public sealed class ChatHistory
{
    private readonly List<ChatMessage> _messages = [];

    /// <summary>历史消息上限。超过后自动截断早期消息（保留 system prompt）。</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>当前历史消息只读视图。</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>当前消息数量。</summary>
    public int Count => _messages.Count;

    /// <summary>Token 预算追踪器。记录 provider 返回的 token 用量。</summary>
    public TokenBudgetTracker Budget { get; } = new();

    /// <summary>可写消息列表。供 ContextCompressor 做索引修改。</summary>
    internal List<ChatMessage> WritableMessages => _messages;

    /// <summary>追加单条消息。追加后若超出 MaxMessages 则截断。</summary>
    /// <param name="message">要追加的消息。</param>
    public void Add(ChatMessage message)
    {
        _messages.Add(message);
        TruncateIfNeeded();
    }

    /// <summary>批量追加消息。追加后若超出 MaxMessages 则截断。</summary>
    /// <param name="messages">要追加的消息集合。</param>
    public void AddRange(IEnumerable<ChatMessage> messages)
    {
        _messages.AddRange(messages);
        TruncateIfNeeded();
    }

    /// <summary>清空所有历史消息。</summary>
    public void Clear() => _messages.Clear();

    /// <summary>构建 LLM 请求的消息列表（system prompt + 历史）。</summary>
    /// <param name="systemPrompt">可选的 system 提示词，为空则不添加。</param>
    /// <returns>用于 LLM 请求的消息列表。</returns>
    public List<ChatMessage> BuildMessages(string? systemPrompt)
    {
        var result = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
            result.Add(ChatMessage.System(systemPrompt));
        result.AddRange(_messages);
        return result;
    }

    // ─── 持久化支持 ───

    /// <summary>
    /// 导出历史为可序列化的列表（用于持久化）。
    /// </summary>
    public List<SerializableMessage> Export() =>
        _messages.Select(m => new SerializableMessage
        {
            Role = m.Role,
            Content = m.Content,
            ContentParts = m.ContentParts?.Select(ClonePart).ToArray(),
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls?.Select(CloneToolCall).ToArray(),
        }).ToList();

    /// <summary>
    /// 从持久化数据导入历史。
    /// </summary>
    public void Import(IEnumerable<SerializableMessage> messages)
    {
        _messages.Clear();
        foreach (var m in messages)
            _messages.Add(new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ContentParts = m.ContentParts?.Select(ClonePart).ToArray(),
                ToolCallId = m.ToolCallId,
                ToolCalls = m.ToolCalls?.Select(CloneToolCall).ToArray(),
            });
    }

    private void TruncateIfNeeded()
    {
        if (_messages.Count <= MaxMessages) return;
        var toRemove = _messages.Count - MaxMessages;
        _messages.RemoveRange(0, toRemove);
    }

    private static ContentPart ClonePart(ContentPart source) => new()
    {
        Kind = source.Kind,
        Text = source.Text,
        DataBase64 = source.DataBase64,
        MediaType = source.MediaType,
        Uri = source.Uri,
        Name = source.Name,
    };

    private static ToolCall CloneToolCall(ToolCall source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ArgumentsJson = source.ArgumentsJson,
    };
}

/// <summary>可序列化的消息（用于 JSON 持久化）。</summary>
public sealed class SerializableMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ContentPart>? ContentParts { get; init; }
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
}

/// <summary>
/// Token 预算追踪器。基于 LLM provider 返回的 TokenUsage 累计用量，
/// 判断是否超出 prompt token 预算阈值。
/// </summary>
public sealed class TokenBudgetTracker
{
    /// <summary>最近一次 LLM 调用的 prompt token 数。</summary>
    public int LastPromptTokens { get; private set; }

    /// <summary>最近一次 LLM 调用的 completion token 数。</summary>
    public int LastCompletionTokens { get; private set; }

    /// <summary>累计 prompt token 数。</summary>
    public int CumulativePromptTokens { get; private set; }

    /// <summary>累计 completion token 数。</summary>
    public int CumulativeCompletionTokens { get; private set; }

    /// <summary>LLM 调用次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>记录一次 LLM 调用的 token 用量。</summary>
    public void RecordUsage(TokenUsage? usage)
    {
        if (usage == null) return;
        LastPromptTokens = usage.PromptTokens;
        LastCompletionTokens = usage.CompletionTokens;
        CumulativePromptTokens += usage.PromptTokens;
        CumulativeCompletionTokens += usage.CompletionTokens;
        CallCount++;
    }

    /// <summary>判断是否超出 prompt token 预算。</summary>
    /// <param name="budgetLimit">Token 预算上限。0 或负数表示禁用。</param>
    /// <param name="threshold">触发压缩的阈值比例（0.0~1.0）。</param>
    public bool IsOverBudget(int budgetLimit, double threshold = 0.85)
    {
        if (budgetLimit <= 0 || CallCount == 0) return false;
        return LastPromptTokens > budgetLimit * threshold;
    }

    /// <summary>重置所有状态。</summary>
    public void Reset()
    {
        LastPromptTokens = 0;
        LastCompletionTokens = 0;
        CumulativePromptTokens = 0;
        CumulativeCompletionTokens = 0;
        CallCount = 0;
    }
}
