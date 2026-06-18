namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Maintains the provider wire invariant that assistant tool calls and tool
/// results must be retained as adjacent, complete pairs.
/// </summary>
public static class ChatMessageToolCallTranscript
{
    public static List<ChatMessage> WithoutInvalidToolCallPairs(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var source = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var result = new List<ChatMessage>(source.Count);

        for (var i = 0; i < source.Count;)
        {
            var message = source[i];
            if (IsAssistantToolCallMessage(message))
            {
                var nextIndex = AppendCompleteToolCallGroup(source, i, result);
                i = nextIndex;
                continue;
            }

            if (IsToolResultMessage(message))
            {
                i++;
                continue;
            }

            result.Add(message);
            i++;
        }

        return result;
    }

    public static int RemoveInvalidToolCallPairs(List<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var sanitized = WithoutInvalidToolCallPairs(messages);
        if (sanitized.Count == messages.Count)
            return 0;

        var removed = messages.Count - sanitized.Count;
        messages.Clear();
        messages.AddRange(sanitized);
        return removed;
    }

    private static int AppendCompleteToolCallGroup(
        IReadOnlyList<ChatMessage> source,
        int assistantIndex,
        List<ChatMessage> result)
    {
        var assistant = source[assistantIndex];
        var expectedIds = CollectExpectedToolCallIds(assistant);
        var blockEnd = FindToolResultBlockEnd(source, assistantIndex + 1);
        if (expectedIds is null || blockEnd == assistantIndex + 1)
            return blockEnd;

        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        var matchedToolMessages = new List<ChatMessage>();
        for (var i = assistantIndex + 1; i < blockEnd; i++)
        {
            var toolCallId = Normalize(source[i].ToolCallId);
            if (toolCallId is null || !expectedIds.Contains(toolCallId) || !matchedIds.Add(toolCallId))
                continue;

            matchedToolMessages.Add(source[i]);
        }

        if (matchedIds.Count != expectedIds.Count)
            return blockEnd;

        result.Add(assistant);
        result.AddRange(matchedToolMessages);
        return blockEnd;
    }

    private static HashSet<string>? CollectExpectedToolCallIds(ChatMessage assistant)
    {
        var calls = assistant.ToolCalls;
        if (calls is not { Count: > 0 })
            return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            var id = Normalize(call.Id);
            if (id is null || !ids.Add(id))
                return null;
        }

        return ids;
    }

    private static int FindToolResultBlockEnd(IReadOnlyList<ChatMessage> source, int start)
    {
        var index = start;
        while (index < source.Count && IsToolResultMessage(source[index]))
            index++;
        return index;
    }

    private static bool IsAssistantToolCallMessage(ChatMessage message) =>
        string.Equals(message.Role, "assistant", StringComparison.Ordinal)
        && message.ToolCalls is { Count: > 0 };

    private static bool IsToolResultMessage(ChatMessage message) =>
        string.Equals(message.Role, "tool", StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
