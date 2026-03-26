using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class AgentFeedReplyLoopHandler : IAgentFeedHintHandler
{
    private readonly IAgentFeedCommandPort _feedCommandPort;
    private readonly IGroupThreadQueryPort _threadQueryPort;
    private readonly IGroupThreadCommandPort _threadCommandPort;
    private readonly IParticipantReplyGenerationPort _replyGenerationPort;
    private readonly IParticipantRuntimeDispatchPort _runtimeDispatchPort;
    private readonly IGroupParticipantReplyProjectionPort _replyProjectionPort;

    public AgentFeedReplyLoopHandler(
        IAgentFeedCommandPort feedCommandPort,
        IGroupThreadQueryPort threadQueryPort,
        IGroupThreadCommandPort threadCommandPort,
        IParticipantReplyGenerationPort replyGenerationPort,
        IParticipantRuntimeDispatchPort runtimeDispatchPort,
        IGroupParticipantReplyProjectionPort replyProjectionPort)
    {
        _feedCommandPort = feedCommandPort ?? throw new ArgumentNullException(nameof(feedCommandPort));
        _threadQueryPort = threadQueryPort ?? throw new ArgumentNullException(nameof(threadQueryPort));
        _threadCommandPort = threadCommandPort ?? throw new ArgumentNullException(nameof(threadCommandPort));
        _replyGenerationPort = replyGenerationPort ?? throw new ArgumentNullException(nameof(replyGenerationPort));
        _runtimeDispatchPort = runtimeDispatchPort ?? throw new ArgumentNullException(nameof(runtimeDispatchPort));
        _replyProjectionPort = replyProjectionPort ?? throw new ArgumentNullException(nameof(replyProjectionPort));
    }

    public async Task HandleAsync(AgentFeedHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var thread = await _threadQueryPort.GetThreadAsync(hint.GroupId, hint.ThreadId, ct);
        if (thread == null)
            return;

        var triggerMessage = thread.Messages.FirstOrDefault(x =>
            string.Equals(x.MessageId, hint.SignalId, StringComparison.Ordinal));
        if (triggerMessage == null)
            return;

        var replyMessageId = GroupParticipantReplyMessageIds.FromSource(hint.AgentId, hint.SourceEventId);
        if (thread.Messages.Any(x => string.Equals(x.MessageId, replyMessageId, StringComparison.Ordinal)))
        {
            await AdvanceAsync(hint.AgentId, hint.SignalId, ct);
            return;
        }

        var runtimeBinding = thread.ParticipantRuntimeBindings.FirstOrDefault(x =>
            string.Equals(x.ParticipantAgentId, hint.AgentId, StringComparison.Ordinal));
        if (runtimeBinding != null)
        {
            var dispatch = await _runtimeDispatchPort.DispatchAsync(
                new ParticipantRuntimeDispatchRequest(
                    hint.GroupId,
                    hint.ThreadId,
                    hint.AgentId,
                    hint.SourceEventId,
                    hint.SourceStateVersion,
                    hint.TimelineCursor,
                    triggerMessage,
                    thread,
                    runtimeBinding),
                ct);
            if (dispatch != null)
            {
                await _replyProjectionPort.EnsureParticipantReplyProjectionAsync(dispatch.RootActorId, dispatch.SessionId, ct);
                await AdvanceAsync(hint.AgentId, hint.SignalId, ct);
            }

            return;
        }

        var reply = await _replyGenerationPort.GenerateReplyAsync(
            new ParticipantReplyGenerationRequest(
                hint.GroupId,
                hint.ThreadId,
                hint.AgentId,
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
            await _threadCommandPort.AppendAgentMessageAsync(
                new AppendAgentMessageCommand
                {
                    GroupId = hint.GroupId,
                    ThreadId = hint.ThreadId,
                    MessageId = replyMessageId,
                    ParticipantAgentId = hint.AgentId,
                    Text = reply.ReplyText,
                    ReplyToMessageId = hint.SignalId,
                    TopicId = hint.TopicId,
                    SignalKind = GroupSignalKind.Result,
                    DerivedFromSignalIds =
                    {
                        hint.SignalId,
                    },
                },
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }

        await AdvanceAsync(hint.AgentId, hint.SignalId, ct);
    }

    private Task AdvanceAsync(string agentId, string signalId, CancellationToken ct) =>
        _feedCommandPort.AdvanceCursorAsync(
            new AdvanceFeedCursorCommand
            {
                AgentId = agentId,
                SignalId = signalId,
            },
            ct);
}
