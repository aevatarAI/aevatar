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
        if (source.ToolResultView is not null)
            target.ToolResultView = ToProto(source.ToolResultView);
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
            ToolResultView = FromProto(source.ToolResultView),
        };
    }

    private static AgentRunToolResultView ToProto(ToolResultView source)
    {
        var target = new AgentRunToolResultView
        {
            ToolName = source.ToolName ?? string.Empty,
        };
        if (source.SkillSearch is not null)
        {
            target.SkillSearch = new AgentRunSkillSearchToolResultView
            {
                Status = ToProto(source.SkillSearch.Status),
                HasMatches = source.SkillSearch.HasMatches,
                Error = source.SkillSearch.Error ?? string.Empty,
                DisplayText = source.SkillSearch.DisplayText ?? string.Empty,
            };
            if (source.SkillSearch.HttpStatus.HasValue)
                target.SkillSearch.HttpStatus = source.SkillSearch.HttpStatus.Value;
            target.SkillSearch.Matches.AddRange(source.SkillSearch.Matches.Select(static match =>
            {
                var mapped = new AgentRunSkillSearchMatchView
                {
                    SkillName = match.SkillName ?? string.Empty,
                    Description = match.Description ?? string.Empty,
                    IsPrivate = match.IsPrivate,
                    Category = match.Category ?? string.Empty,
                };
                mapped.Tags.AddRange(match.Tags);
                return mapped;
            }));
        }

        if (source.SkillLoad is not null)
        {
            target.SkillLoad = new AgentRunSkillLoadToolResultView
            {
                Status = ToProto(source.SkillLoad.Status),
                SkillName = source.SkillLoad.SkillName ?? string.Empty,
                Loaded = source.SkillLoad.Loaded,
                Error = source.SkillLoad.Error ?? string.Empty,
                DisplayText = source.SkillLoad.DisplayText ?? string.Empty,
            };
            if (source.SkillLoad.HttpStatus.HasValue)
                target.SkillLoad.HttpStatus = source.SkillLoad.HttpStatus.Value;
        }

        if (source.Failure is not null)
        {
            target.Failure = new AgentRunToolFailureResultView
            {
                Status = source.Failure.Status,
                ErrorCode = source.Failure.ErrorCode ?? string.Empty,
                SafeMessage = source.Failure.SafeMessage ?? string.Empty,
            };
        }

        return target;
    }

    private static ToolResultView? FromProto(AgentRunToolResultView? source)
    {
        if (source is null)
            return null;

        return new ToolResultView(
            source.ToolName ?? string.Empty,
            source.SkillSearch is null
                ? null
                : new SkillSearchToolResultView(
                    FromProto(source.SkillSearch.Status),
                    source.SkillSearch.HasMatches,
                    source.SkillSearch.Matches.Select(static match => new SkillSearchMatchView(
                        match.SkillName ?? string.Empty,
                        Normalize(match.Description),
                        match.IsPrivate,
                        Normalize(match.Category),
                        match.Tags.ToArray())).ToArray(),
                    Normalize(source.SkillSearch.Error),
                    source.SkillSearch.HasHttpStatus ? source.SkillSearch.HttpStatus : null,
                    source.SkillSearch.DisplayText ?? string.Empty),
            source.SkillLoad is null
                ? null
                : new SkillLoadToolResultView(
                    FromProto(source.SkillLoad.Status),
                    Normalize(source.SkillLoad.SkillName),
                    source.SkillLoad.Loaded,
                    Normalize(source.SkillLoad.Error),
                    source.SkillLoad.HasHttpStatus ? source.SkillLoad.HttpStatus : null,
                    source.SkillLoad.DisplayText ?? string.Empty),
            source.Failure is null
                ? null
                : new ToolFailureResultView(
                    source.Failure.Status,
                    source.Failure.ErrorCode ?? string.Empty,
                    source.Failure.SafeMessage ?? string.Empty));
    }

    private static AgentRunToolResultViewStatus ToProto(ToolResultViewStatus source) =>
        source switch
        {
            ToolResultViewStatus.Success => AgentRunToolResultViewStatus.Success,
            ToolResultViewStatus.NoMatch => AgentRunToolResultViewStatus.NoMatch,
            ToolResultViewStatus.NotFound => AgentRunToolResultViewStatus.NotFound,
            ToolResultViewStatus.Error => AgentRunToolResultViewStatus.Error,
            _ => AgentRunToolResultViewStatus.Unknown,
        };

    private static ToolResultViewStatus FromProto(AgentRunToolResultViewStatus source) =>
        source switch
        {
            AgentRunToolResultViewStatus.Success => ToolResultViewStatus.Success,
            AgentRunToolResultViewStatus.NoMatch => ToolResultViewStatus.NoMatch,
            AgentRunToolResultViewStatus.NotFound => ToolResultViewStatus.NotFound,
            AgentRunToolResultViewStatus.Error => ToolResultViewStatus.Error,
            _ => ToolResultViewStatus.Unknown,
        };

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
