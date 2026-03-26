using Aevatar.GroupChat.Abstractions.Feeds;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedInterestEvaluator
{
    Task<AgentFeedInterestDecision?> EvaluateAsync(GroupMentionHint hint, CancellationToken ct = default);
}
