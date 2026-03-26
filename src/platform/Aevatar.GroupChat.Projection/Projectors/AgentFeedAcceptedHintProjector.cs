using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class AgentFeedAcceptedHintProjector : IProjectionArtifactMaterializer<AgentFeedProjectionContext>
{
    private readonly IAgentFeedHintPublisher _publisher;

    public AgentFeedAcceptedHintProjector(IAgentFeedHintPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public async ValueTask ProjectAsync(
        AgentFeedProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<AgentFeedState>(
                envelope,
                out var published,
                out var stateEvent,
                out _) ||
            published?.StateEvent?.EventData == null ||
            stateEvent == null ||
            !published.StateEvent.EventData.Is(FeedSignalAcceptedEvent.Descriptor))
        {
            return;
        }

        var evt = published.StateEvent.EventData.Unpack<FeedSignalAcceptedEvent>();
        await _publisher.PublishAsync(
            new AgentFeedHint
            {
                AgentId = evt.AgentId,
                SignalId = evt.SignalId,
                SourceEventId = evt.SourceEventId,
                GroupId = evt.GroupId,
                ThreadId = evt.ThreadId,
                TopicId = evt.TopicId,
                SenderKind = evt.SenderKind,
                SenderId = evt.SenderId,
                SignalKind = evt.SignalKind,
                SourceStateVersion = evt.SourceStateVersion,
                TimelineCursor = evt.TimelineCursor,
                AcceptReason = evt.AcceptReason,
                RankScore = evt.RankScore,
            },
            ct);
    }
}
