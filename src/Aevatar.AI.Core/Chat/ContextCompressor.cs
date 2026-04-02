// ─────────────────────────────────────────────────────────────
// ContextCompressor — 多层级上下文压缩
//
// Level 1: Tool Result 压缩（去重 + 截断）
// Level 2: 重要性感知截断（评分 + 移除低分消息）
// Level 3: LLM 摘要压缩（调用 LLM 摘要最旧消息块）
// ─────────────────────────────────────────────────────────────

using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Chat;

/// <summary>上下文压缩器。三级渐进式压缩，操作 List&lt;ChatMessage&gt; 原地修改。</summary>
public static class ContextCompressor
{
    // ─── Level 1: Tool Result 压缩 ───

    /// <summary>
    /// 压缩 tool result 消息：去重相同内容 + 截断超长结果。
    /// </summary>
    /// <param name="messages">消息列表（原地修改）。</param>
    /// <param name="maxToolResultLength">单个 tool result 最大字符数。</param>
    /// <returns>被修改的消息数量。</returns>
    public static int CompactToolResults(List<ChatMessage> messages, int maxToolResultLength = 4000)
    {
        var modified = 0;
        // content hash → first tool call id
        var seen = new Dictionary<string, string>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (!string.Equals(msg.Role, "tool", StringComparison.Ordinal) || msg.Content == null)
                continue;

            var hash = ComputeContentHash(msg.Content);

            if (seen.TryGetValue(hash, out var firstCallId))
            {
                messages[i] = new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = msg.ToolCallId,
                    Content = $"[duplicate of tool call {firstCallId}]",
                };
                modified++;
                continue;
            }

            seen[hash] = msg.ToolCallId ?? $"index-{i}";

            if (msg.Content.Length > maxToolResultLength)
            {
                messages[i] = new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = msg.ToolCallId,
                    Content = msg.Content[..maxToolResultLength] + "\n...[compressed]",
                };
                modified++;
            }
        }

        return modified;
    }

    // ─── Level 2: 重要性感知截断 ───

    /// <summary>
    /// 按重要性评分截断消息列表至目标数量。
    /// System 消息和最近 N 条消息不可移除。
    /// Tool call 和对应 tool result 作为原子组处理。
    /// </summary>
    /// <param name="messages">消息列表（原地修改）。</param>
    /// <param name="targetCount">目标消息数量。</param>
    /// <param name="preserveRecentCount">尾部保护的消息数。</param>
    /// <returns>被移除的消息数量。</returns>
    public static int TruncateByImportance(List<ChatMessage> messages, int targetCount, int preserveRecentCount = 6)
    {
        if (messages.Count <= targetCount) return 0;

        // Build tool call dependency set: ToolCallId values that have matching tool result messages
        var toolResultCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.Ordinal) && msg.ToolCallId != null)
                toolResultCallIds.Add(msg.ToolCallId);
        }

        // Build set of assistant message indices that have active tool calls
        var assistantToolCallIndices = new HashSet<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].Role, "assistant", StringComparison.Ordinal)
                && messages[i].ToolCalls is { Count: > 0 })
            {
                var hasMatchingResult = false;
                foreach (var tc in messages[i].ToolCalls!)
                {
                    if (toolResultCallIds.Contains(tc.Id))
                    {
                        hasMatchingResult = true;
                        break;
                    }
                }

                if (hasMatchingResult)
                    assistantToolCallIndices.Add(i);
            }
        }

        // Score each message
        var protectedStart = Math.Max(0, messages.Count - preserveRecentCount);
        var scores = new (int Index, float Score)[messages.Count];

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            // System messages and recent messages are protected
            if (string.Equals(msg.Role, "system", StringComparison.Ordinal) || i >= protectedStart)
            {
                scores[i] = (i, float.MaxValue);
                continue;
            }

            var recency = (float)i / messages.Count;
            var roleWeight = msg.Role switch
            {
                "user" => 2.0f,
                "assistant" => 1.5f,
                _ => 1.0f, // tool
            };

            var structuralBonus = 0f;

            // Tool result messages that have a matching assistant tool_call get a bonus
            if (string.Equals(msg.Role, "tool", StringComparison.Ordinal) && msg.ToolCallId != null)
                structuralBonus = 5.0f;

            // Assistant messages with tool calls that have matching results get a bonus
            if (assistantToolCallIndices.Contains(i))
                structuralBonus = 5.0f;

            scores[i] = (i, recency + roleWeight + structuralBonus);
        }

        // Sort by score ascending — lowest scored first for removal
        var sortedByScore = scores
            .Where(s => s.Score < float.MaxValue)
            .OrderBy(s => s.Score)
            .ToList();

        var toRemove = messages.Count - targetCount;
        var indicesToRemove = new HashSet<int>();

        foreach (var (index, _) in sortedByScore)
        {
            if (indicesToRemove.Count >= toRemove) break;

            // For tool result messages, also remove the paired assistant tool_call message
            // For assistant tool_call messages, also remove paired tool results
            if (string.Equals(messages[index].Role, "tool", StringComparison.Ordinal))
            {
                indicesToRemove.Add(index);
            }
            else if (string.Equals(messages[index].Role, "assistant", StringComparison.Ordinal)
                     && messages[index].ToolCalls is { Count: > 0 })
            {
                // Remove the assistant + all its tool result messages as a group
                indicesToRemove.Add(index);
                var callIds = new HashSet<string>(
                    messages[index].ToolCalls!.Select(tc => tc.Id),
                    StringComparer.Ordinal);
                for (var j = index + 1; j < messages.Count && j < protectedStart; j++)
                {
                    if (string.Equals(messages[j].Role, "tool", StringComparison.Ordinal)
                        && messages[j].ToolCallId != null
                        && callIds.Contains(messages[j].ToolCallId!))
                    {
                        indicesToRemove.Add(j);
                    }
                }
            }
            else
            {
                indicesToRemove.Add(index);
            }
        }

        // Remove from highest index downward to preserve indices
        foreach (var idx in indicesToRemove.OrderByDescending(x => x))
            messages.RemoveAt(idx);

        return indicesToRemove.Count;
    }

    // ─── Level 3: LLM 摘要压缩 ───

    private const string SummarizationSystemPrompt =
        "You are a conversation summarizer. Summarize the following conversation block concisely, " +
        "preserving key facts, decisions, tool results, and context needed for continuation. " +
        "Output only the summary, no preamble.";

    /// <summary>
    /// 调用 LLM 将最旧的消息块摘要为单条消息。
    /// </summary>
    /// <param name="messages">消息列表（原地修改）。</param>
    /// <param name="provider">LLM provider（直接调用，不走 hooks/middleware）。</param>
    /// <param name="model">可选模型名称。</param>
    /// <param name="blockSize">摘要的消息块大小。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功执行了摘要。</returns>
    public static async Task<bool> SummarizeOldestBlockAsync(
        List<ChatMessage> messages,
        ILLMProvider provider,
        string? model = null,
        int blockSize = 8,
        CancellationToken ct = default)
    {
        // Find first non-system message
        var startIndex = 0;
        while (startIndex < messages.Count
               && string.Equals(messages[startIndex].Role, "system", StringComparison.Ordinal))
        {
            startIndex++;
        }

        // Need at least blockSize messages beyond the start, plus a preserve window
        if (messages.Count - startIndex < blockSize + 4)
            return false;

        // Extract the block to summarize
        var block = messages.GetRange(startIndex, blockSize);
        var blockText = new StringBuilder();
        foreach (var msg in block)
        {
            blockText.Append(msg.Role).Append(": ");
            blockText.AppendLine(msg.Content ?? "[no content]");
        }

        var request = new LLMRequest
        {
            Messages =
            [
                ChatMessage.System(SummarizationSystemPrompt),
                ChatMessage.User(blockText.ToString()),
            ],
            Model = model,
            Tools = null,
            MaxTokens = 1024,
        };

        var response = await provider.ChatAsync(request, ct);
        if (string.IsNullOrWhiteSpace(response.Content))
            return false;

        // Remove the block and insert summary
        messages.RemoveRange(startIndex, blockSize);
        messages.Insert(startIndex, ChatMessage.System($"[Previous conversation summary]: {response.Content}"));
        return true;
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
