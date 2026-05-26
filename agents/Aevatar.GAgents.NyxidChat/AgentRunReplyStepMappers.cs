using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf.Collections;

namespace Aevatar.GAgents.NyxidChat;

internal static class AgentRunReplyStepMappers
{
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

    public static Dictionary<string, string> ToDictionary(MapField<string, string> source) =>
        new(source, StringComparer.Ordinal);

    public static LLMControlContext LlmControlFromProto(AgentRunReplyStepState state) =>
        LLMControlContextMapper.FromPayload(state.LlmControl);

    public static AgentToolExecutionContext ToolContextFromProto(AgentRunReplyStepState state) =>
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
