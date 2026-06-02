using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public interface IConversationReplyGenerator
{
    /// <summary>
    /// Generates the full LLM reply for one Lark / channel turn and returns the actor-edge
    /// closeout for the streaming run.
    /// </summary>
    /// <remarks>
    /// Per ADR-0021 §6 / canon §8 the run-level streaming closeout lives at the actor edge,
    /// not inside <c>ChatRuntime</c>. Implementations MUST:
    /// <list type="bullet">
    ///   <item>Aggregate <c>Usage</c> across all internal LLM rounds (tool-call loop), returning
    ///   the sum on <see cref="ConversationReplyResult.Usage"/>.</item>
    ///   <item>Surface the last non-empty <c>FinishReason</c> observed across rounds on
    ///   <see cref="ConversationReplyResult.FinishReason"/>.</item>
    ///   <item>Forward progressive deltas to <paramref name="streamingSink"/> when supplied; tolerate
    ///   a null sink by accumulating into the final text.</item>
    /// </list>
    /// Round-internal terminal markers (per-round <c>IsLast</c> / per-round <c>Usage</c>) MUST NOT
    /// escape this boundary — callers consume a single closeout via the returned record.
    /// </remarks>
    Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        IStreamingReplySink? streamingSink,
        CancellationToken ct);
}

/// <summary>
/// Single actor-edge closeout for a streaming reply run. ADR-0021 §6 / canon §8.
/// </summary>
/// <param name="Text">The final reply text accumulated across all internal LLM rounds.</param>
/// <param name="Usage">Cross-round token usage sum, or <c>null</c> when no provider reported it.</param>
/// <param name="FinishReason">The last non-empty finish reason observed across rounds, or <c>null</c>.</param>
public sealed record ConversationReplyResult(
    string? Text,
    ReplyTokenUsage? Usage,
    string? FinishReason,
    IReadOnlyList<ConversationHistoryEntry>? AppendedHistory = null);

/// <summary>
/// Channel-runtime-side token usage projection. Mirrors <c>Aevatar.AI.Abstractions.LLMProviders.TokenUsage</c>
/// to avoid a layer-violating dependency from Channel.Runtime onto AI.Abstractions
/// (see CLAUDE.md "依赖反转").
/// </summary>
public sealed record ReplyTokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
