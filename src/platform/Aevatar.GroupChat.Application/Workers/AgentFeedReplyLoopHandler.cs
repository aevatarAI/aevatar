using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class AgentFeedReplyLoopHandler : IAgentFeedHintHandler
{
    private readonly IParticipantReplyRunCommandPort _replyRunCommandPort;

    public AgentFeedReplyLoopHandler(IParticipantReplyRunCommandPort replyRunCommandPort)
    {
        _replyRunCommandPort = replyRunCommandPort ?? throw new ArgumentNullException(nameof(replyRunCommandPort));
    }

    public Task HandleAsync(AgentFeedHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        return _replyRunCommandPort.StartAsync(
            new StartParticipantReplyRunCommand
            {
                GroupId = hint.GroupId,
                ThreadId = hint.ThreadId,
                ParticipantAgentId = hint.AgentId,
                SignalId = hint.SignalId,
                SourceEventId = hint.SourceEventId,
                SourceStateVersion = hint.SourceStateVersion,
                TimelineCursor = hint.TimelineCursor,
                TopicId = hint.TopicId ?? string.Empty,
            },
            ct);
    }
}
