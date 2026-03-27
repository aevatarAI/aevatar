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
            state == null)
        {
            return;
        }

        var hint = BuildHintEnvelope(published.StateEvent.EventData, stateEvent, state);
        if (hint == null)
            return;

        foreach (var participantAgentId in hint.ParticipantAgentIds)
        {
            await _publisher.PublishAsync(
                new GroupMentionHint
                {
                    GroupId = hint.GroupId,
                    ThreadId = hint.ThreadId,
                    MessageId = hint.MessageId,
                    ParticipantAgentId = participantAgentId,
                    SourceEventId = hint.SourceEventId,
                    SourceStateVersion = hint.SourceStateVersion,
                    TimelineCursor = hint.TimelineCursor,
                    DirectHintAgentIds =
                    {
                        hint.ParticipantAgentIds,
                    },
                    TopicId = hint.TopicId,
                    SenderKind = hint.SenderKind,
                    SenderId = hint.SenderId,
                    SignalKind = hint.SignalKind,
                    SourceIds =
                    {
                        hint.SourceIds,
                    },
                    SourceKinds =
                    {
                        hint.SourceKinds,
                    },
                    EvidenceRefCount = hint.EvidenceRefCount,
                },
                ct);
        }
    }

    private static MentionHintEnvelope? BuildHintEnvelope(
        Google.Protobuf.WellKnownTypes.Any payload,
        StateEvent stateEvent,
        GroupThreadState state)
    {
        if (payload.Is(UserMessagePostedEvent.Descriptor))
        {
            var evt = payload.Unpack<UserMessagePostedEvent>();
            return BuildHintEnvelope(
                evt.GroupId,
                evt.ThreadId,
                evt.MessageId,
                evt.DirectHintAgentIds,
                evt.TopicId,
                GroupMessageSenderKind.User,
                evt.SenderUserId,
                evt.SignalKind,
                ResolveTimelineCursor(state, evt.MessageId),
                stateEvent.EventId ?? string.Empty,
                stateEvent.Version,
                ResolveSourceIds(evt.SourceRefs, evt.EvidenceRefs),
                evt.SourceRefs.Select(static x => x.SourceKind).Distinct().ToList(),
                evt.EvidenceRefs.Count);
        }

        if (payload.Is(AgentMessageAppendedEvent.Descriptor))
        {
            var evt = payload.Unpack<AgentMessageAppendedEvent>();
            return BuildHintEnvelope(
                evt.GroupId,
                evt.ThreadId,
                evt.MessageId,
                evt.DirectHintAgentIds,
                evt.TopicId,
                GroupMessageSenderKind.Agent,
                evt.ParticipantAgentId,
                evt.SignalKind,
                ResolveTimelineCursor(state, evt.MessageId),
                stateEvent.EventId ?? string.Empty,
                stateEvent.Version,
                ResolveSourceIds(evt.SourceRefs, evt.EvidenceRefs),
                evt.SourceRefs.Select(static x => x.SourceKind).Distinct().ToList(),
                evt.EvidenceRefs.Count);
        }

        return null;
    }

    private static MentionHintEnvelope? BuildHintEnvelope(
        string groupId,
        string threadId,
        string messageId,
        Google.Protobuf.Collections.RepeatedField<string> directHintAgentIds,
        string? topicId,
        GroupMessageSenderKind senderKind,
        string? senderId,
        GroupSignalKind signalKind,
        long timelineCursor,
        string sourceEventId,
        long sourceStateVersion,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<GroupSourceKind> sourceKinds,
        int evidenceRefCount)
    {
        var participantAgentIds = directHintAgentIds
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (participantAgentIds.Count == 0)
            return null;

        return new MentionHintEnvelope(
            groupId,
            threadId,
            messageId,
            participantAgentIds,
            topicId?.Trim() ?? string.Empty,
            senderKind,
            senderId?.Trim() ?? string.Empty,
            signalKind,
            timelineCursor,
            sourceEventId,
            sourceStateVersion,
            sourceIds,
            sourceKinds,
            evidenceRefCount);
    }

    private static long ResolveTimelineCursor(GroupThreadState state, string messageId) =>
        state.MessageEntries
            .FirstOrDefault(x => string.Equals(x.MessageId, messageId, StringComparison.Ordinal))
            ?.TimelineCursor ?? 0;

    private static IReadOnlyList<string> ResolveSourceIds(
        Google.Protobuf.Collections.RepeatedField<GroupSourceRef> sourceRefs,
        Google.Protobuf.Collections.RepeatedField<GroupEvidenceRef> evidenceRefs) =>
        sourceRefs
            .Select(static x => x.SourceId?.Trim() ?? string.Empty)
            .Concat(evidenceRefs.Select(static x => x.SourceId?.Trim() ?? string.Empty))
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private sealed record MentionHintEnvelope(
        string GroupId,
        string ThreadId,
        string MessageId,
        IReadOnlyList<string> ParticipantAgentIds,
        string TopicId,
        GroupMessageSenderKind SenderKind,
        string SenderId,
        GroupSignalKind SignalKind,
        long TimelineCursor,
        string SourceEventId,
        long SourceStateVersion,
        IReadOnlyList<string> SourceIds,
        IReadOnlyList<GroupSourceKind> SourceKinds,
        int EvidenceRefCount);
}
