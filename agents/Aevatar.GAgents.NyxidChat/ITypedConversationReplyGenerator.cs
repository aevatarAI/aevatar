using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

internal interface ITypedConversationReplyGenerator : IConversationReplyGenerator
{
    Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IStreamingReplySink? streamingSink,
        CancellationToken ct);

    Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        IStreamingReplySink? streamingSink,
        CancellationToken ct) =>
        GenerateReplyAsync(activity, metadata, llmControl, toolContext, streamingSink, ct);
}

internal interface IAgentRunStepConversationReplyGenerator : ITypedConversationReplyGenerator
{
    Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        ChatAttachmentInputContext? attachmentContext,
        bool forceDisableTools,
        CancellationToken ct,
        AgentTurnToolCatalog? turnCatalog = null);

    MessageContent? TryTakeOutboundIntent() => null;
}

// Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
// slash silently consumed.
// New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
// non-slash text path unchanged (owner-LLM chat fallback).
public sealed record AgentRunReplyStepPlan(
    ChatRuntimeStepExecutor StepExecutor,
    IReadOnlyDictionary<string, string> Metadata,
    LLMControlContext LlmControl,
    AgentToolExecutionContext ToolContext,
    IReadOnlyList<ChatMessage> InitialMessages,
    int MaxToolRounds,
    bool DisableTools = false,
    LLMControlContext? OwnerFallbackLlmControl = null,
    AgentToolExecutionContext? OwnerFallbackToolContext = null);

internal sealed record ChatAttachmentInputContext(
    IReadOnlyList<RecentConversationAttachmentActivity> RecentAttachmentActivities,
    string? UserAccessToken);
