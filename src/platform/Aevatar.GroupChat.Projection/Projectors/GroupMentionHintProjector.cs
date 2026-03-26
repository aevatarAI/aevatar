using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class GroupMentionHintProjector : IProjectionArtifactMaterializer<GroupTimelineProjectionContext>
{
    private readonly IGroupMentionHintPublisher _publisher;

    public GroupMentionHintProjector(IGroupMentionHintPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public async ValueTask ProjectAsync(
        GroupTimelineProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<GroupThreadState>(
                envelope,
                out var published,
                out var stateEvent,
                out var state) ||
            published?.StateEvent?.EventData == null ||
            stateEvent == null ||
            state == null ||
            !published.StateEvent.EventData.Is(UserMessagePostedEvent.Descriptor))
        {
            return;
        }

        var evt = published.StateEvent.EventData.Unpack<UserMessagePostedEvent>();
        if (evt.DirectHintAgentIds.Count == 0)
            return;

        var timelineCursor = state.MessageEntries
            .FirstOrDefault(x => string.Equals(x.MessageId, evt.MessageId, StringComparison.Ordinal))
            ?.TimelineCursor ?? 0;
        var participantAgentIds = evt.DirectHintAgentIds
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var participantAgentId in participantAgentIds)
        {
            await _publisher.PublishAsync(
                new GroupMentionHint
                {
                    GroupId = evt.GroupId,
                    ThreadId = evt.ThreadId,
                    MessageId = evt.MessageId,
                    ParticipantAgentId = participantAgentId,
                    SourceEventId = stateEvent.EventId ?? string.Empty,
                    SourceStateVersion = stateEvent.Version,
                    TimelineCursor = timelineCursor,
                    DirectHintAgentIds =
                    {
                        participantAgentIds,
                    },
                    TopicId = evt.TopicId ?? string.Empty,
                    SenderKind = GroupMessageSenderKind.User,
                    SenderId = evt.SenderUserId ?? string.Empty,
                    SignalKind = evt.SignalKind,
                    SourceIds =
                    {
                        ResolveSourceIds(evt),
                    },
                    SourceKinds =
                    {
                        evt.SourceRefs
                            .Select(static x => x.SourceKind)
                            .Distinct()
                    },
                    EvidenceRefCount = evt.EvidenceRefs.Count,
                },
                ct);
        }
    }

    private static IEnumerable<string> ResolveSourceIds(UserMessagePostedEvent evt) =>
        evt.SourceRefs
            .Select(static x => x.SourceId?.Trim() ?? string.Empty)
            .Concat(evt.EvidenceRefs.Select(static x => x.SourceId?.Trim() ?? string.Empty))
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);
}
