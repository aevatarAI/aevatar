using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class GroupParticipantReplyCompletedService
{
    private readonly IStreamProvider _streamProvider;
    private readonly IGroupThreadCommandPort _commandPort;
    private readonly IGroupParticipantReplyProjectionPort _projectionPort;

    public GroupParticipantReplyCompletedService(
        IStreamProvider streamProvider,
        IGroupThreadCommandPort commandPort,
        IGroupParticipantReplyProjectionPort projectionPort)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public Task<IAsyncDisposable> SubscribeAsync(CancellationToken ct = default) =>
        _streamProvider.GetStream(GroupParticipantReplyCompletedStreamIds.Global).SubscribeAsync<GroupParticipantReplyCompletedEvent>(
            evt => HandleAsync(evt, ct),
            ct);

    private async Task HandleAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.Content))
            return;

        try
        {
            await _commandPort.AppendAgentMessageAsync(
                new AppendAgentMessageCommand
                {
                    GroupId = evt.GroupId,
                    ThreadId = evt.ThreadId,
                    MessageId = evt.ReplyMessageId,
                    ParticipantAgentId = evt.ParticipantAgentId,
                    Text = evt.Content,
                    ReplyToMessageId = evt.ReplyToMessageId,
                    TopicId = evt.TopicId,
                    SignalKind = GroupSignalKind.Result,
                    DerivedFromSignalIds =
                    {
                        evt.ReplyToMessageId,
                    },
                },
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            // Duplicate replay is tolerated by deterministic reply message ids.
        }
        finally
        {
            await _projectionPort.ReleaseParticipantReplyProjectionAsync(
                evt.RootActorId,
                evt.SessionId,
                ct);
        }
    }
}
