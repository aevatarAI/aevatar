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
            ToolCallId = m.ToolCallId,
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
                ToolCallId = m.ToolCallId,
            });
    }

    private void TruncateIfNeeded()
    {
        if (_messages.Count <= MaxMessages) return;
        var toRemove = _messages.Count - MaxMessages;
        _messages.RemoveRange(0, toRemove);
    }
}

/// <summary>可序列化的消息（用于 JSON 持久化）。</summary>
public sealed class SerializableMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
}
