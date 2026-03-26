using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class AgentFeedCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentFeedProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentFeedReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public AgentFeedCurrentStateProjector(
        IProjectionWriteDispatcher<AgentFeedReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AgentFeedProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<AgentFeedState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new AgentFeedReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            AgentId = state.AgentId,
            FeedCursor = state.FeedCursor,
            NextItems = state.NextItemEntries
                .Select(static item => new AgentFeedItemReadModel
                {
                    SignalId = item.SignalId,
                    GroupId = item.GroupId,
                    ThreadId = item.ThreadId,
                    TopicId = item.TopicId,
                    SenderKindValue = (int)item.SenderKind,
                    SenderId = item.SenderId,
                    SignalKindValue = (int)item.SignalKind,
                    SourceEventId = item.SourceEventId,
                    SourceStateVersion = item.SourceStateVersion,
                    TimelineCursor = item.TimelineCursor,
                    AcceptReasonValue = (int)item.AcceptReason,
                    RankScore = item.RankScore,
                })
                .ToList(),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
