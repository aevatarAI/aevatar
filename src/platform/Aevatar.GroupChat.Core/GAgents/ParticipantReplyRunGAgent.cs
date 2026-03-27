using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;
using Google.Protobuf;

namespace Aevatar.GroupChat.Core.GAgents;

public sealed class ParticipantReplyRunGAgent : GAgentBase<ParticipantReplyRunState>
{
    private readonly IAgentFeedCommandPort _feedCommandPort;
    private readonly IGroupThreadQueryPort _threadQueryPort;
    private readonly IGroupThreadCommandPort _threadCommandPort;
    private readonly IParticipantReplyGenerationPort _replyGenerationPort;
    private readonly IParticipantRuntimeDispatchPort _runtimeDispatchPort;
    private readonly IGroupParticipantReplyProjectionPort _replyProjectionPort;

    public ParticipantReplyRunGAgent(
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
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartAsync(StartParticipantReplyRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateStart(command);
        EnsureCompatible(command);
        if (IsTerminal(State.Status) || State.Status == GroupParticipantReplyRunStatus.AwaitingCompletion)
            return;

        var replyMessageId = string.IsNullOrWhiteSpace(State.ReplyMessageId)
            ? GroupParticipantReplyMessageIds.FromSource(command.ParticipantAgentId, command.SourceEventId)
            : State.ReplyMessageId;
        if (!IsBound())
        {
            await PersistDomainEventAsync(new ParticipantReplyRunStartedEvent
            {
                GroupId = command.GroupId,
                ThreadId = command.ThreadId,
                ParticipantAgentId = command.ParticipantAgentId,
                SignalId = command.SignalId,
                SourceEventId = command.SourceEventId,
                SourceStateVersion = command.SourceStateVersion,
                TimelineCursor = command.TimelineCursor,
                TopicId = command.TopicId ?? string.Empty,
                ReplyMessageId = replyMessageId,
            });
        }

        var thread = await _threadQueryPort.GetThreadAsync(command.GroupId, command.ThreadId, CancellationToken.None);
        if (thread == null)
        {
            await PersistFailureAsync(command, replyMessageId, "group thread snapshot not found.");
            return;
        }

        var triggerMessage = thread.Messages.FirstOrDefault(x =>
            string.Equals(x.MessageId, command.SignalId, StringComparison.Ordinal));
        if (triggerMessage == null)
        {
            await PersistFailureAsync(command, replyMessageId, "trigger message snapshot not found.");
            return;
        }

        if (thread.Messages.Any(x => string.Equals(x.MessageId, replyMessageId, StringComparison.Ordinal)))
        {
            await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
            await PersistDomainEventAsync(BuildCompletedEvent(command, replyMessageId));
            return;
        }

        var runtimeBinding = thread.ParticipantRuntimeBindings.FirstOrDefault(x =>
            string.Equals(x.ParticipantAgentId, command.ParticipantAgentId, StringComparison.Ordinal));
        if (runtimeBinding != null)
        {
            var dispatch = await _runtimeDispatchPort.DispatchAsync(
                new ParticipantRuntimeDispatchRequest(
                    command.GroupId,
                    command.ThreadId,
                    command.ParticipantAgentId,
                    command.SourceEventId,
                    command.SourceStateVersion,
                    command.TimelineCursor,
                    triggerMessage,
                    thread,
                    runtimeBinding),
                CancellationToken.None);
            if (dispatch == null)
            {
                await PersistFailureAsync(command, replyMessageId, "participant runtime dispatch returned no accepted result.");
                return;
            }

            if (dispatch.CompletionMode == ParticipantRuntimeCompletionMode.SyncCompleted)
            {
                if (string.IsNullOrWhiteSpace(dispatch.ReplyText))
                {
                    await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
                    await PersistDomainEventAsync(BuildNoContentEvent(command, replyMessageId));
                    return;
                }

                await AppendReplyAsync(
                    command.GroupId,
                    command.ThreadId,
                    replyMessageId,
                    command.ParticipantAgentId,
                    dispatch.ReplyText,
                    command.SignalId,
                    command.TopicId ?? string.Empty);
                await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
                await PersistDomainEventAsync(BuildCompletedEvent(command, replyMessageId));
                return;
            }

            if (dispatch.CompletionMode == ParticipantRuntimeCompletionMode.AsyncObserved)
            {
                await _replyProjectionPort.EnsureParticipantReplyProjectionAsync(
                    dispatch.RootActorId,
                    dispatch.SessionId,
                    CancellationToken.None);
                await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
                await PersistDomainEventAsync(new ParticipantReplyRunAsyncDispatchAcceptedEvent
                {
                    GroupId = command.GroupId,
                    ThreadId = command.ThreadId,
                    ParticipantAgentId = command.ParticipantAgentId,
                    SignalId = command.SignalId,
                    SourceEventId = command.SourceEventId,
                    TopicId = command.TopicId ?? string.Empty,
                    ReplyMessageId = replyMessageId,
                    RootActorId = dispatch.RootActorId ?? string.Empty,
                    SessionId = dispatch.SessionId ?? string.Empty,
                });
                return;
            }

            await PersistFailureAsync(
                command,
                replyMessageId,
                $"participant runtime completion mode '{dispatch.CompletionMode}' is not supported.");
            return;
        }

        var reply = await _replyGenerationPort.GenerateReplyAsync(
            new ParticipantReplyGenerationRequest(
                command.GroupId,
                command.ThreadId,
                command.ParticipantAgentId,
                command.SourceEventId,
                command.SourceStateVersion,
                command.TimelineCursor,
                triggerMessage,
                thread),
            CancellationToken.None);
        if (reply == null || string.IsNullOrWhiteSpace(reply.ReplyText))
        {
            await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
            await PersistDomainEventAsync(BuildNoContentEvent(command, replyMessageId));
            return;
        }

        await AppendReplyAsync(
            command.GroupId,
            command.ThreadId,
            replyMessageId,
            command.ParticipantAgentId,
            reply.ReplyText,
            command.SignalId,
            command.TopicId ?? string.Empty);
        await AdvanceAsync(command.ParticipantAgentId, command.SignalId);
        await PersistDomainEventAsync(BuildCompletedEvent(command, replyMessageId));
    }

    [EventHandler]
    public async Task HandleCompleteAsync(CompleteParticipantReplyRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateComplete(command);
        EnsureCompatible(command);
        if (IsTerminal(State.Status))
            return;

        var rootActorId = !string.IsNullOrWhiteSpace(State.RootActorId) ? State.RootActorId : command.RootActorId;
        var sessionId = !string.IsNullOrWhiteSpace(State.SessionId) ? State.SessionId : command.SessionId;
        var replyMessageId = !string.IsNullOrWhiteSpace(State.ReplyMessageId) ? State.ReplyMessageId : command.ReplyMessageId;
        try
        {
            if (string.IsNullOrWhiteSpace(command.Content))
            {
                await PersistDomainEventAsync(BuildNoContentEvent(command, replyMessageId));
                return;
            }

            await AppendReplyAsync(
                command.GroupId,
                command.ThreadId,
                replyMessageId,
                command.ParticipantAgentId,
                command.Content,
                command.ReplyToMessageId,
                command.TopicId ?? string.Empty);
            await PersistDomainEventAsync(BuildCompletedEvent(command, replyMessageId));
        }
        finally
        {
            await _replyProjectionPort.ReleaseParticipantReplyProjectionAsync(
                rootActorId,
                sessionId,
                CancellationToken.None);
        }
    }

    protected override ParticipantReplyRunState TransitionState(ParticipantReplyRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ParticipantReplyRunStartedEvent>(ApplyStarted)
            .On<ParticipantReplyRunAsyncDispatchAcceptedEvent>(ApplyAwaitingCompletion)
            .On<ParticipantReplyRunCompletedEvent>(ApplyCompleted)
            .On<ParticipantReplyRunNoContentEvent>(ApplyNoContent)
            .On<ParticipantReplyRunFailedEvent>(ApplyFailed)
            .OrCurrent();

    private static ParticipantReplyRunState ApplyStarted(ParticipantReplyRunState state, ParticipantReplyRunStartedEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.ParticipantAgentId = evt.ParticipantAgentId;
        next.SignalId = evt.SignalId;
        next.SourceEventId = evt.SourceEventId;
        next.TopicId = evt.TopicId ?? string.Empty;
        next.ReplyMessageId = evt.ReplyMessageId ?? string.Empty;
        next.RootActorId = string.Empty;
        next.SessionId = string.Empty;
        next.Status = GroupParticipantReplyRunStatus.Started;
        next.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, evt.ParticipantAgentId, evt.SourceEventId, "started");
        return next;
    }

    private static ParticipantReplyRunState ApplyAwaitingCompletion(ParticipantReplyRunState state, ParticipantReplyRunAsyncDispatchAcceptedEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.ParticipantAgentId = evt.ParticipantAgentId;
        next.SignalId = evt.SignalId;
        next.SourceEventId = evt.SourceEventId;
        next.TopicId = evt.TopicId ?? string.Empty;
        next.ReplyMessageId = evt.ReplyMessageId ?? string.Empty;
        next.RootActorId = evt.RootActorId ?? string.Empty;
        next.SessionId = evt.SessionId ?? string.Empty;
        next.Status = GroupParticipantReplyRunStatus.AwaitingCompletion;
        next.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, evt.ParticipantAgentId, evt.SourceEventId, "awaiting");
        return next;
    }

    private static ParticipantReplyRunState ApplyCompleted(ParticipantReplyRunState state, ParticipantReplyRunCompletedEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.ParticipantAgentId = evt.ParticipantAgentId;
        next.SignalId = evt.SignalId;
        next.SourceEventId = evt.SourceEventId;
        next.TopicId = evt.TopicId ?? string.Empty;
        next.ReplyMessageId = evt.ReplyMessageId ?? string.Empty;
        next.Status = GroupParticipantReplyRunStatus.Completed;
        next.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, evt.ParticipantAgentId, evt.SourceEventId, "completed");
        return next;
    }

    private static ParticipantReplyRunState ApplyNoContent(ParticipantReplyRunState state, ParticipantReplyRunNoContentEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.ParticipantAgentId = evt.ParticipantAgentId;
        next.SignalId = evt.SignalId;
        next.SourceEventId = evt.SourceEventId;
        next.TopicId = evt.TopicId ?? string.Empty;
        next.ReplyMessageId = evt.ReplyMessageId ?? string.Empty;
        next.Status = GroupParticipantReplyRunStatus.NoContent;
        next.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, evt.ParticipantAgentId, evt.SourceEventId, "no-content");
        return next;
    }

    private static ParticipantReplyRunState ApplyFailed(ParticipantReplyRunState state, ParticipantReplyRunFailedEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.ParticipantAgentId = evt.ParticipantAgentId;
        next.SignalId = evt.SignalId;
        next.SourceEventId = evt.SourceEventId;
        next.TopicId = evt.TopicId ?? string.Empty;
        next.ReplyMessageId = evt.ReplyMessageId ?? string.Empty;
        next.Status = GroupParticipantReplyRunStatus.Failed;
        next.FailureReason = evt.FailureReason ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, evt.ParticipantAgentId, evt.SourceEventId, "failed");
        return next;
    }

    private async Task AppendReplyAsync(
        string groupId,
        string threadId,
        string replyMessageId,
        string participantAgentId,
        string text,
        string replyToMessageId,
        string topicId)
    {
        try
        {
            await _threadCommandPort.AppendAgentMessageAsync(
                new AppendAgentMessageCommand
                {
                    GroupId = groupId,
                    ThreadId = threadId,
                    MessageId = replyMessageId,
                    ParticipantAgentId = participantAgentId,
                    Text = text,
                    ReplyToMessageId = replyToMessageId,
                    TopicId = topicId ?? string.Empty,
                    SignalKind = GroupSignalKind.Result,
                    DerivedFromSignalIds =
                    {
                        replyToMessageId,
                    },
                },
                CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }
    }

    private Task AdvanceAsync(string participantAgentId, string signalId) =>
        _feedCommandPort.AdvanceCursorAsync(
            new AdvanceFeedCursorCommand
            {
                AgentId = participantAgentId,
                SignalId = signalId,
            },
            CancellationToken.None);

    private Task PersistFailureAsync(
        StartParticipantReplyRunCommand command,
        string replyMessageId,
        string failureReason) =>
        PersistDomainEventAsync(new ParticipantReplyRunFailedEvent
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            ParticipantAgentId = command.ParticipantAgentId,
            SignalId = command.SignalId,
            SourceEventId = command.SourceEventId,
            TopicId = command.TopicId ?? string.Empty,
            ReplyMessageId = replyMessageId,
            FailureReason = failureReason,
        });

    private static ParticipantReplyRunCompletedEvent BuildCompletedEvent(
        StartParticipantReplyRunCommand command,
        string replyMessageId) =>
        new()
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            ParticipantAgentId = command.ParticipantAgentId,
            SignalId = command.SignalId,
            SourceEventId = command.SourceEventId,
            TopicId = command.TopicId ?? string.Empty,
            ReplyMessageId = replyMessageId,
        };

    private static ParticipantReplyRunCompletedEvent BuildCompletedEvent(
        CompleteParticipantReplyRunCommand command,
        string replyMessageId) =>
        new()
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            ParticipantAgentId = command.ParticipantAgentId,
            SignalId = command.ReplyToMessageId,
            SourceEventId = command.SourceEventId,
            TopicId = command.TopicId ?? string.Empty,
            ReplyMessageId = replyMessageId,
        };

    private static ParticipantReplyRunNoContentEvent BuildNoContentEvent(
        StartParticipantReplyRunCommand command,
        string replyMessageId) =>
        new()
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            ParticipantAgentId = command.ParticipantAgentId,
            SignalId = command.SignalId,
            SourceEventId = command.SourceEventId,
            TopicId = command.TopicId ?? string.Empty,
            ReplyMessageId = replyMessageId,
        };

    private static ParticipantReplyRunNoContentEvent BuildNoContentEvent(
        CompleteParticipantReplyRunCommand command,
        string replyMessageId) =>
        new()
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            ParticipantAgentId = command.ParticipantAgentId,
            SignalId = command.ReplyToMessageId,
            SourceEventId = command.SourceEventId,
            TopicId = command.TopicId ?? string.Empty,
            ReplyMessageId = replyMessageId,
        };

    private static void ValidateStart(StartParticipantReplyRunCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ParticipantAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SignalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceEventId);
    }

    private static void ValidateComplete(CompleteParticipantReplyRunCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ParticipantAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ReplyMessageId);
    }

    private void EnsureCompatible(StartParticipantReplyRunCommand command)
    {
        if (!IsBound())
            return;

        if (!string.Equals(State.GroupId, command.GroupId, StringComparison.Ordinal) ||
            !string.Equals(State.ThreadId, command.ThreadId, StringComparison.Ordinal) ||
            !string.Equals(State.ParticipantAgentId, command.ParticipantAgentId, StringComparison.Ordinal) ||
            !string.Equals(State.SourceEventId, command.SourceEventId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Participant reply run actor '{Id}' is already bound to another reply session.");
        }
    }

    private void EnsureCompatible(CompleteParticipantReplyRunCommand command)
    {
        if (!IsBound())
            return;

        if (!string.Equals(State.GroupId, command.GroupId, StringComparison.Ordinal) ||
            !string.Equals(State.ThreadId, command.ThreadId, StringComparison.Ordinal) ||
            !string.Equals(State.ParticipantAgentId, command.ParticipantAgentId, StringComparison.Ordinal) ||
            !string.Equals(State.SourceEventId, command.SourceEventId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Participant reply run actor '{Id}' is already bound to another reply session.");
        }

        if (!string.IsNullOrWhiteSpace(State.SessionId) &&
            !string.IsNullOrWhiteSpace(command.SessionId) &&
            !string.Equals(State.SessionId, command.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Participant reply run actor '{Id}' is bound to session '{State.SessionId}', but got '{command.SessionId}'.");
        }
    }

    private bool IsBound() =>
        !string.IsNullOrWhiteSpace(State.GroupId) &&
        !string.IsNullOrWhiteSpace(State.ThreadId) &&
        !string.IsNullOrWhiteSpace(State.ParticipantAgentId) &&
        !string.IsNullOrWhiteSpace(State.SourceEventId);

    private static bool IsTerminal(GroupParticipantReplyRunStatus status) =>
        status == GroupParticipantReplyRunStatus.Completed ||
        status == GroupParticipantReplyRunStatus.NoContent ||
        status == GroupParticipantReplyRunStatus.Failed;

    private static string BuildEventId(
        string groupId,
        string threadId,
        string participantAgentId,
        string sourceEventId,
        string suffix) =>
        $"reply-run:{groupId}:{threadId}:{participantAgentId}:{sourceEventId}:{suffix}";
}
