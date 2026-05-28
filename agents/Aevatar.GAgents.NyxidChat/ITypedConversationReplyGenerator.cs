using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
