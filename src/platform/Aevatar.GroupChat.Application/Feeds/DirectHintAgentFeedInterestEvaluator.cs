using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Feeds;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Feeds;

public sealed class DirectHintAgentFeedInterestEvaluator : IAgentFeedInterestEvaluator
{
    private const int DirectHintScore = 100;

    public Task<AgentFeedInterestDecision?> EvaluateAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        AgentFeedInterestDecision? decision = hint.DirectHintAgentIds.Contains(hint.ParticipantAgentId)
            ? new AgentFeedInterestDecision(DirectHintScore, GroupFeedAcceptReason.DirectHint)
            : null;
        return Task.FromResult(decision);
    }
}
