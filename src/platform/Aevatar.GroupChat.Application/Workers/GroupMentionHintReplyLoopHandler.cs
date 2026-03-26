using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class GroupMentionHintReplyLoopHandler : IGroupMentionHintHandler
{
    private readonly IGroupThreadQueryPort _queryPort;
    private readonly IGroupThreadCommandPort _commandPort;
    private readonly IParticipantReplyGenerationPort _replyGenerationPort;
    private readonly IParticipantRuntimeDispatchPort _runtimeDispatchPort;
    private readonly IGroupParticipantReplyProjectionPort _replyProjectionPort;

    public GroupMentionHintReplyLoopHandler(
        IGroupThreadQueryPort queryPort,
        IGroupThreadCommandPort commandPort,
        IParticipantReplyGenerationPort replyGenerationPort,
        IParticipantRuntimeDispatchPort runtimeDispatchPort,
        IGroupParticipantReplyProjectionPort replyProjectionPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _replyGenerationPort = replyGenerationPort ?? throw new ArgumentNullException(nameof(replyGenerationPort));
        _runtimeDispatchPort = runtimeDispatchPort ?? throw new ArgumentNullException(nameof(runtimeDispatchPort));
        _replyProjectionPort = replyProjectionPort ?? throw new ArgumentNullException(nameof(replyProjectionPort));
    }

    public async Task HandleAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var thread = await _queryPort.GetThreadAsync(hint.GroupId, hint.ThreadId, ct);
        if (thread == null)
            return;

        var triggerMessage = thread.Messages.FirstOrDefault(x =>
            string.Equals(x.MessageId, hint.MessageId, StringComparison.Ordinal));
        if (triggerMessage == null ||
            !triggerMessage.DirectHintAgentIds.Contains(hint.ParticipantAgentId))
        {
            return;
        }

        var replyMessageId = BuildReplyMessageId(hint.ParticipantAgentId, hint.SourceEventId);
        if (thread.Messages.Any(x => string.Equals(x.MessageId, replyMessageId, StringComparison.Ordinal)))
            return;

        var runtimeBinding = thread.ParticipantRuntimeBindings.FirstOrDefault(x =>
            string.Equals(x.ParticipantAgentId, hint.ParticipantAgentId, StringComparison.Ordinal));
        if (runtimeBinding != null)
        {
            var dispatch = await _runtimeDispatchPort.DispatchAsync(
                new ParticipantRuntimeDispatchRequest(
                    hint.GroupId,
                    hint.ThreadId,
                    hint.ParticipantAgentId,
                    hint.SourceEventId,
                    hint.SourceStateVersion,
                    hint.TimelineCursor,
                    triggerMessage,
                    thread,
                    runtimeBinding),
                ct);
            if (dispatch != null)
                await _replyProjectionPort.EnsureParticipantReplyProjectionAsync(dispatch.RootActorId, dispatch.SessionId, ct);
            return;
        }

        var reply = await _replyGenerationPort.GenerateReplyAsync(
            new ParticipantReplyGenerationRequest(
                hint.GroupId,
                hint.ThreadId,
                hint.ParticipantAgentId,
                hint.SourceEventId,
                hint.SourceStateVersion,
                hint.TimelineCursor,
                triggerMessage,
                thread),
            ct);
        if (reply == null || string.IsNullOrWhiteSpace(reply.ReplyText))
            return;

        try
        {
            await _commandPort.AppendAgentMessageAsync(
                new AppendAgentMessageCommand
                {
                    GroupId = hint.GroupId,
                    ThreadId = hint.ThreadId,
                    MessageId = replyMessageId,
                    ParticipantAgentId = hint.ParticipantAgentId,
                    Text = reply.ReplyText,
                    ReplyToMessageId = hint.MessageId,
                    TopicId = triggerMessage.TopicId,
                    SignalKind = GroupSignalKind.Result,
                    DerivedFromSignalIds =
                    {
                        triggerMessage.MessageId,
                    },
                },
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            // Duplicate hint delivery is tolerated by using a deterministic reply message id.
        }
    }

    internal static string BuildReplyMessageId(string participantAgentId, string sourceEventId)
    {
        return GroupParticipantReplyMessageIds.FromSource(participantAgentId, sourceEventId);
    }
}
