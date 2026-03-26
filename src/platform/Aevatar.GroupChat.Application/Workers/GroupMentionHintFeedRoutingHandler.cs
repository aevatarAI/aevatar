using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class GroupMentionHintFeedRoutingHandler : IGroupMentionHintHandler
{
    private readonly IAgentFeedCommandPort _feedCommandPort;
    private readonly IAgentFeedInterestEvaluator _interestEvaluator;

    public GroupMentionHintFeedRoutingHandler(
        IAgentFeedCommandPort feedCommandPort,
        IAgentFeedInterestEvaluator interestEvaluator)
    {
        _feedCommandPort = feedCommandPort ?? throw new ArgumentNullException(nameof(feedCommandPort));
        _interestEvaluator = interestEvaluator ?? throw new ArgumentNullException(nameof(interestEvaluator));
    }

    public async Task HandleAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var decision = await _interestEvaluator.EvaluateAsync(hint, ct);
        if (decision == null)
            return;

        await _feedCommandPort.AcceptSignalAsync(
            new AcceptSignalToFeedCommand
            {
                AgentId = hint.ParticipantAgentId,
                SignalId = hint.MessageId,
                GroupId = hint.GroupId,
                ThreadId = hint.ThreadId,
                TopicId = hint.TopicId,
                SenderKind = hint.SenderKind,
                SenderId = hint.SenderId,
                SignalKind = hint.SignalKind,
                SourceEventId = hint.SourceEventId,
                SourceStateVersion = hint.SourceStateVersion,
                TimelineCursor = hint.TimelineCursor,
                AcceptReason = decision.AcceptReason,
                RankScore = decision.Score,
            },
            ct);
    }
}
