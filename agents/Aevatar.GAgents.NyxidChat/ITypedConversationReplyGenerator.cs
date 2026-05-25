using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
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
}

internal interface IAgentRunStepConversationReplyGenerator : ITypedConversationReplyGenerator
{
    Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct);

    MessageContent? TryTakeOutboundIntent() => null;
}

public sealed record AgentRunReplyStepPlan(
    ChatRuntimeStepExecutor StepExecutor,
    IReadOnlyDictionary<string, string> Metadata,
    LLMControlContext LlmControl,
    AgentToolExecutionContext ToolContext,
    IReadOnlyList<ChatMessage> InitialMessages,
    int MaxToolRounds);
