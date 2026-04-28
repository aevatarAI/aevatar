using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public interface IConversationReplyGenerator
{
    /// <summary>
    /// Generates the full LLM reply text. If <paramref name="streamingSink"/> is supplied, the
    /// generator forwards progressive deltas as the stream advances; implementations must tolerate
    /// a null sink by simply accumulating the final text.
    /// </summary>
    Task<string?> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        IStreamingReplySink? streamingSink,
        CancellationToken ct);
}
