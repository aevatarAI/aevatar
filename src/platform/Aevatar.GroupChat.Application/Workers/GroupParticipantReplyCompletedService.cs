using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class GroupParticipantReplyCompletedService
{
    private readonly IStreamProvider _streamProvider;
    private readonly IParticipantReplyRunCommandPort _replyRunCommandPort;

    public GroupParticipantReplyCompletedService(
        IStreamProvider streamProvider,
        IParticipantReplyRunCommandPort replyRunCommandPort)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _replyRunCommandPort = replyRunCommandPort ?? throw new ArgumentNullException(nameof(replyRunCommandPort));
    }

    public Task<IAsyncDisposable> SubscribeAsync(CancellationToken ct = default) =>
        _streamProvider.GetStream(GroupParticipantReplyCompletedStreamIds.Global).SubscribeAsync<GroupParticipantReplyCompletedEvent>(
            evt => HandleAsync(evt, ct),
            ct);

    private async Task HandleAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await _replyRunCommandPort.CompleteAsync(
            new CompleteParticipantReplyRunCommand
            {
                RootActorId = evt.RootActorId,
                SessionId = evt.SessionId,
                GroupId = evt.GroupId,
                ThreadId = evt.ThreadId,
                ReplyToMessageId = evt.ReplyToMessageId,
                ParticipantAgentId = evt.ParticipantAgentId,
                SourceEventId = evt.SourceEventId,
                ReplyMessageId = evt.ReplyMessageId,
                Content = evt.Content,
                TopicId = evt.TopicId,
            },
            ct);
    }
}
