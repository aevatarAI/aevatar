using Aevatar.AI.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class GroupParticipantReplySessionProjector
    : IProjectionProjector<GroupParticipantReplyProjectionContext>
{
    private readonly IGroupParticipantReplyCompletedPublisher _publisher;

    public GroupParticipantReplySessionProjector(IGroupParticipantReplyCompletedPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public async ValueTask ProjectAsync(
        GroupParticipantReplyProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null)
        {
            return;
        }

        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (!BelongsToObservedRoot(context.RootActorId, publisherActorId))
            return;

        var completed = ResolveCompletedSession(payload);
        if (completed == null ||
            !GroupParticipantRuntimeSessionId.TryParse(completed.Value.SessionId, out var session) ||
            !string.Equals(context.SessionId, completed.Value.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        await _publisher.PublishAsync(
            new GroupParticipantReplyCompletedEvent
            {
                RootActorId = context.RootActorId,
                SessionId = completed.Value.SessionId,
                GroupId = session.GroupId,
                ThreadId = session.ThreadId,
                TopicId = session.TopicId,
                ReplyToMessageId = session.ReplyToMessageId,
                ParticipantAgentId = session.ParticipantAgentId,
                SourceEventId = session.SourceEventId,
                ReplyMessageId = GroupParticipantReplyMessageIds.FromSource(session.ParticipantAgentId, session.SourceEventId),
                Content = completed.Value.Content,
            },
            ct);
    }

    private static (string SessionId, string Content)? ResolveCompletedSession(Google.Protobuf.WellKnownTypes.Any payload)
    {
        if (payload.Is(RoleChatSessionCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<RoleChatSessionCompletedEvent>();
            return (completed.SessionId, completed.Content ?? string.Empty);
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var completed = payload.Unpack<TextMessageEndEvent>();
            return (completed.SessionId, completed.Content ?? string.Empty);
        }

        return null;
    }

    private static bool BelongsToObservedRoot(string rootActorId, string publisherActorId) =>
        !string.IsNullOrWhiteSpace(rootActorId) &&
        !string.IsNullOrWhiteSpace(publisherActorId) &&
        (string.Equals(rootActorId, publisherActorId, StringComparison.Ordinal) ||
         publisherActorId.StartsWith(rootActorId + ":", StringComparison.Ordinal));
}
