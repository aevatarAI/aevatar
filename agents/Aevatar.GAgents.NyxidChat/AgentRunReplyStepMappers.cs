using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf.Collections;

namespace Aevatar.GAgents.NyxidChat;

internal static class AgentRunReplyStepMappers
{
    // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
    //   Old pattern: AgentRunReplyGenerationExecutor cloned/mutated AgentRunReplyStepState and the actor persisted that full state.
    //   New principle: Executor returns typed IO facts; actor applies deterministic step-state transition inside event handling.
    public static AgentRunChatMessage ToProto(ChatMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new AgentRunChatMessage
        {
            Role = source.Role,
            Content = source.Content ?? string.Empty,
            ReasoningContent = source.ReasoningContent ?? string.Empty,
            ToolCallId = source.ToolCallId ?? string.Empty,
        };
        target.ContentParts.AddRange(ContentPartProtoMapper.ToProtoList(source.ContentParts));
        if (source.ToolCalls is { Count: > 0 })
            target.ToolCalls.AddRange(source.ToolCalls.Select(ToProto));
        return target;
    }

    public static AgentRunChatMessage ToProto(Aevatar.GAgents.Channel.Runtime.ConversationHistoryEntry source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new AgentRunChatMessage
        {
            Role = string.IsNullOrWhiteSpace(source.Role) ? "user" : source.Role,
            Content = source.Content ?? string.Empty,
            ReasoningContent = source.ReasoningContent ?? string.Empty,
            ToolCallId = source.ToolCallId ?? string.Empty,
        };
        target.ContentParts.AddRange(source.ContentParts);
        target.ToolCalls.AddRange(source.ToolCalls.Select(ToProto));
        return target;
    }

    public static ChatMessage FromProto(AgentRunChatMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ChatMessage
        {
            Role = string.IsNullOrWhiteSpace(source.Role) ? "user" : source.Role,
            Content = Normalize(source.Content),
            ReasoningContent = Normalize(source.ReasoningContent),
            ContentParts = source.ContentParts.Count == 0
                ? null
                : ContentPartProtoMapper.FromProtoList(source.ContentParts),
            ToolCallId = Normalize(source.ToolCallId),
            ToolCalls = source.ToolCalls.Count == 0
                ? null
                : source.ToolCalls.Select(FromProto).ToArray(),
        };
    }

    public static AgentRunToolCall ToProto(ToolCall source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AgentRunToolCall
        {
            Id = source.Id,
            Name = source.Name,
            ArgumentsJson = source.ArgumentsJson,
        };
    }

    public static AgentRunToolCall ToProto(Aevatar.GAgents.Channel.Runtime.ConversationToolCallEntry source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AgentRunToolCall
        {
            Id = source.Id ?? string.Empty,
            Name = source.Name ?? string.Empty,
            ArgumentsJson = source.ArgumentsJson ?? string.Empty,
        };
    }

    public static ToolCall FromProto(AgentRunToolCall source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ToolCall
        {
            Id = source.Id,
            Name = source.Name,
            ArgumentsJson = source.ArgumentsJson,
        };
    }

    public static Aevatar.GAgents.Channel.Runtime.ConversationHistoryEntry ToConversationHistoryEntry(
        AgentRunChatMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new Aevatar.GAgents.Channel.Runtime.ConversationHistoryEntry
        {
            Role = source.Role ?? string.Empty,
            Content = source.Content ?? string.Empty,
            ReasoningContent = source.ReasoningContent ?? string.Empty,
            ToolCallId = source.ToolCallId ?? string.Empty,
        };
        target.ContentParts.AddRange(source.ContentParts);
        target.ToolCalls.AddRange(source.ToolCalls.Select(ToConversationToolCallEntry));
        return target;
    }

    private static Aevatar.GAgents.Channel.Runtime.ConversationToolCallEntry ToConversationToolCallEntry(
        AgentRunToolCall source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Aevatar.GAgents.Channel.Runtime.ConversationToolCallEntry
        {
            Id = source.Id ?? string.Empty,
            Name = source.Name ?? string.Empty,
            ArgumentsJson = source.ArgumentsJson ?? string.Empty,
        };
    }

    public static Dictionary<string, string> ToDictionary(MapField<string, string> source) =>
        new(source, StringComparer.Ordinal);

    public static LLMControlContext LlmControlFromProto(AgentRunReplyStepState state) =>
        LLMControlContextMapper.FromPayload(state.LlmControl);

    public static AgentToolExecutionContext ToolContextFromProto(AgentRunReplyStepState state) =>
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext);

    // Refactor helper, no behavior change: keep executor/actor result payload conversion narrow.
    public static AgentRunReplyTokenUsage? ToProto(TokenUsage? source) =>
        source is null
            ? null
            : new AgentRunReplyTokenUsage
            {
                PromptTokens = source.PromptTokens,
                CompletionTokens = source.CompletionTokens,
                TotalTokens = source.TotalTokens,
            };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
