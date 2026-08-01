using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text;

namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Per-conversation actor that owns terminal chat history turns for one conversation.
/// Actor ID is an opaque deterministic encoding of the scope and conversation tuple.
/// </summary>
[GAgent("chat.history.conversation")]
public sealed class ChatConversationGAgent : GAgentBase<ChatConversationState>,
    IProjectedActor
{
    public static string ProjectionKind => "chat-conversation";

    private const int MaxSynthesizedConversationTitleLength = 48;
    private const string TitleEllipsis = "…";

    [EventHandler(EndpointName = "initializeChatConversation")]
    public async Task HandleInitializeChatConversation(InitializeChatConversationCommand command)
    {
        ValidateInitialization(command);

        if (State.Initialization is not null)
        {
            if (HasSameInitialization(State.Initialization, command))
                return;

            throw new InvalidOperationException("Chat conversation initialization conflicts with the committed initialization.");
        }

        if (State.Deleted || HasIdentityConflict(State, command))
            throw new InvalidOperationException("Chat conversation initialization conflicts with the current conversation state.");

        await PersistDomainEventAsync(new ChatConversationInitializedEvent
        {
            OperationId = command.OperationId,
            ScopeId = command.ScopeId,
            ConversationId = command.ConversationId,
            ServiceId = command.ServiceId,
            ServiceKind = command.ServiceKind,
            CreatedAt = command.CreatedAt.Clone(),
            InitialTitle = command.InitialTitle,
        });
    }

    [EventHandler(EndpointName = "appendChatTurn")]
    public async Task HandleAppendChatTurn(AppendChatTurnCommand command)
    {
        if (!TryValidateAppend(command, out var rejectionReason))
        {
            await PersistRejectionAsync(command, rejectionReason);
            return;
        }

        var turn = command.Turn.Clone();
        var existing = State.Turns.FirstOrDefault(x =>
            string.Equals(x.TurnId, turn.TurnId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (HasSamePayload(existing, turn))
            {
                await DispatchAppendResultAsync(command, true, ChatTurnAppendRejectionReason.Unspecified);
            }
            else if (CanReconcileTerminal(existing, turn))
            {
                turn.Sequence = existing.Sequence;
                await PersistDomainEventAsync(new ChatTurnTerminalReconciledEvent
                {
                    ScopeId = command.ScopeId,
                    ConversationId = command.ConversationId,
                    PreviousStatus = existing.TerminalStatus,
                    Turn = turn,
                });
                await DispatchAppendResultAsync(command, true, ChatTurnAppendRejectionReason.Unspecified);
            }
            else
            {
                await PersistRejectionAsync(command, ChatTurnAppendRejectionReason.Conflict);
                await DispatchAppendResultAsync(command, false, ChatTurnAppendRejectionReason.Conflict);
            }
            return;
        }

        turn.Sequence = ResolveNextTurnSequence(State);
        await PersistDomainEventAsync(new ChatTurnAppendedEvent
        {
            ScopeId = command.ScopeId,
            ConversationId = command.ConversationId,
            Title = ResolveAppendTitle(command, turn),
            ServiceId = command.ServiceId,
            ServiceKind = command.ServiceKind,
            Turn = turn,
        });
        await DispatchAppendResultAsync(command, true, ChatTurnAppendRejectionReason.Unspecified);
    }

    [EventHandler(EndpointName = "deleteConversation")]
    public async Task HandleDeleteConversation(DeleteConversationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.ConversationId))
            return;

        if (HasDifferentValue(State.ScopeId, command.ScopeId) ||
            HasDifferentValue(State.ConversationId, command.ConversationId))
        {
            throw new InvalidOperationException(
                "Chat conversation deletion conflicts with the current conversation identity.");
        }

        if (State.Deleted || (string.IsNullOrWhiteSpace(State.ConversationId) && State.Turns.Count == 0))
            return;

        await PersistDomainEventAsync(new ConversationDeletedEvent
        {
            ConversationId = command.ConversationId,
            ScopeId = command.ScopeId,
            DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
    }

    protected override ChatConversationState TransitionState(
        ChatConversationState current,
        IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ChatConversationInitializedEvent>(ApplyChatConversationInitialized)
            .On<ChatTurnAppendedEvent>(ApplyChatTurnAppended)
            .On<ChatTurnTerminalReconciledEvent>(ApplyChatTurnTerminalReconciled)
            .On<ChatTurnAppendRejectedEvent>(ApplyChatTurnAppendRejected)
            .On<ConversationDeletedEvent>(ApplyConversationDeleted)
            .OrCurrent();
    }

    private static void ValidateInitialization(InitializeChatConversationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.OperationId) ||
            string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.ConversationId) ||
            string.IsNullOrWhiteSpace(command.ServiceId) ||
            string.IsNullOrWhiteSpace(command.ServiceKind) ||
            command.CreatedAt is null)
        {
            throw new InvalidOperationException("Chat conversation initialization requires stable identity and creation time.");
        }

        _ = command.CreatedAt.ToDateTimeOffset();
    }

    private static bool HasIdentityConflict(
        ChatConversationState state,
        InitializeChatConversationCommand command) =>
        HasDifferentValue(state.ScopeId, command.ScopeId) ||
        HasDifferentValue(state.ConversationId, command.ConversationId) ||
        HasDifferentValue(state.ServiceId, command.ServiceId) ||
        HasDifferentValue(state.ServiceKind, command.ServiceKind);

    private static bool HasDifferentValue(string existing, string candidate) =>
        !string.IsNullOrWhiteSpace(existing) &&
        !string.Equals(existing, candidate, StringComparison.Ordinal);

    private static bool HasSameInitialization(
        ChatConversationInitialization existing,
        InitializeChatConversationCommand candidate) =>
        string.Equals(existing.OperationId, candidate.OperationId, StringComparison.Ordinal) &&
        string.Equals(existing.ScopeId, candidate.ScopeId, StringComparison.Ordinal) &&
        string.Equals(existing.ConversationId, candidate.ConversationId, StringComparison.Ordinal) &&
        string.Equals(existing.ServiceId, candidate.ServiceId, StringComparison.Ordinal) &&
        string.Equals(existing.ServiceKind, candidate.ServiceKind, StringComparison.Ordinal) &&
        Equals(existing.CreatedAt, candidate.CreatedAt) &&
        string.Equals(existing.InitialTitle, candidate.InitialTitle, StringComparison.Ordinal);

    private bool TryValidateAppend(
        AppendChatTurnCommand command,
        out ChatTurnAppendRejectionReason rejectionReason)
    {
        if (command.Turn is null ||
            string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.ConversationId) ||
            string.IsNullOrWhiteSpace(command.Turn.TurnId) ||
            command.Turn.TerminalStatus is ChatTurnTerminalStatus.Unspecified ||
            State.Deleted)
        {
            rejectionReason = ChatTurnAppendRejectionReason.Invalid;
            return false;
        }

        rejectionReason = ChatTurnAppendRejectionReason.Unspecified;
        return true;
    }

    private async Task PersistRejectionAsync(
        AppendChatTurnCommand command,
        ChatTurnAppendRejectionReason reason)
    {
        await PersistDomainEventAsync(new ChatTurnAppendRejectedEvent
        {
            ScopeId = command.ScopeId,
            ConversationId = command.ConversationId,
            TurnId = command.Turn?.TurnId ?? string.Empty,
            Reason = reason,
            RejectedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private async Task DispatchAppendResultAsync(
        AppendChatTurnCommand command,
        bool accepted,
        ChatTurnAppendRejectionReason rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(command.DeliveryActorId))
            return;

        var dispatchPort = Services?.GetService<IActorDispatchPort>();
        if (dispatchPort is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var result = new ChatTurnHistoryDeliveryAppendResultObserved
        {
            DeliveryActorId = command.DeliveryActorId.Trim(),
            ConversationId = command.ConversationId,
            TurnId = command.Turn?.TurnId ?? string.Empty,
            Accepted = accepted,
            RejectionReason = rejectionReason,
            ObservedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(now),
            Payload = Any.Pack(result),
            Route = EnvelopeRouteSemantics.CreateDirect("chat-history-conversation", result.DeliveryActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = result.TurnId,
            },
        };
        await dispatchPort.DispatchAsync(result.DeliveryActorId, envelope, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static bool HasSamePayload(ChatTurn existing, ChatTurn candidate) =>
        string.Equals(existing.UserText, candidate.UserText, StringComparison.Ordinal) &&
        string.Equals(existing.AssistantText, candidate.AssistantText, StringComparison.Ordinal) &&
        existing.TerminalStatus == candidate.TerminalStatus &&
        string.Equals(existing.SanitizedError, candidate.SanitizedError, StringComparison.Ordinal) &&
        string.Equals(existing.LlmRoute, candidate.LlmRoute, StringComparison.Ordinal) &&
        string.Equals(existing.LlmModel, candidate.LlmModel, StringComparison.Ordinal) &&
        Equals(existing.TerminalTime, candidate.TerminalTime);

    private static int ResolveNextTurnSequence(ChatConversationState state)
    {
        if (state.NextTurnSequence > 0)
            return state.NextTurnSequence;

        // Snapshots written before next_turn_sequence existed need a one-time
        // migration from already committed turn identities.
        return state.Turns.Count == 0
            ? 1
            : checked(state.Turns.Max(static turn => turn.Sequence) + 1);
    }

    private static bool CanReconcileTerminal(ChatTurn existing, ChatTurn candidate) =>
        existing.TerminalStatus == ChatTurnTerminalStatus.OutcomeUncertain &&
        candidate.TerminalStatus is ChatTurnTerminalStatus.Completed or ChatTurnTerminalStatus.Failed &&
        string.Equals(existing.UserText, candidate.UserText, StringComparison.Ordinal) &&
        string.Equals(existing.LlmRoute, candidate.LlmRoute, StringComparison.Ordinal) &&
        string.Equals(existing.LlmModel, candidate.LlmModel, StringComparison.Ordinal);

    private string ResolveAppendTitle(AppendChatTurnCommand command, ChatTurn turn)
    {
        if (!string.IsNullOrWhiteSpace(command.Title))
            return command.Title;

        if (!string.IsNullOrWhiteSpace(State.Title) || State.Turns.Count > 0)
            return string.Empty;

        return SynthesizeInitialTitle(turn.UserText);
    }

    private static string SynthesizeInitialTitle(string? userText)
    {
        var normalized = NormalizeTitleSource(userText);
        if (normalized.Length == 0)
            return string.Empty;

        var textElements = new List<string>(MaxSynthesizedConversationTitleLength + 1);
        var enumerator = StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
        {
            textElements.Add((string)enumerator.Current);
            if (textElements.Count > MaxSynthesizedConversationTitleLength)
                break;
        }

        if (textElements.Count <= MaxSynthesizedConversationTitleLength)
            return normalized;

        return string.Concat(textElements.Take(MaxSynthesizedConversationTitleLength - 1)) + TitleEllipsis;
    }

    private static string NormalizeTitleSource(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return string.Empty;

        var builder = new StringBuilder(userText.Length);
        var pendingSpace = false;
        foreach (var ch in userText)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0)
                    pendingSpace = true;
                continue;
            }

            if (char.IsControl(ch))
                continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static ChatConversationState ApplyChatTurnAppended(
        ChatConversationState state,
        ChatTurnAppendedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = evt.ScopeId;
        next.ConversationId = evt.ConversationId;
        if (!string.IsNullOrWhiteSpace(evt.Title))
            next.Title = evt.Title;
        if (!string.IsNullOrWhiteSpace(evt.ServiceId))
            next.ServiceId = evt.ServiceId;
        if (!string.IsNullOrWhiteSpace(evt.ServiceKind))
            next.ServiceKind = evt.ServiceKind;

        var terminalAt = evt.Turn?.TerminalTime?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0;
        if (next.CreatedAtMs == 0)
            next.CreatedAtMs = terminalAt;
        next.UpdatedAtMs = terminalAt;
        next.Deleted = false;
        next.LastRejectedAppend = null;
        if (evt.Turn is not null)
        {
            next.Turns.Add(evt.Turn.Clone());
            next.NextTurnSequence = checked(Math.Max(next.NextTurnSequence, evt.Turn.Sequence + 1));
        }
        return next;
    }

    private static ChatConversationState ApplyChatConversationInitialized(
        ChatConversationState state,
        ChatConversationInitializedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = evt.ScopeId;
        next.ConversationId = evt.ConversationId;
        next.ServiceId = evt.ServiceId;
        next.ServiceKind = evt.ServiceKind;
        if (string.IsNullOrWhiteSpace(next.Title))
            next.Title = evt.InitialTitle;

        var createdAtMs = evt.CreatedAt.ToDateTimeOffset().ToUnixTimeMilliseconds();
        next.CreatedAtMs = createdAtMs;
        next.UpdatedAtMs = Math.Max(next.UpdatedAtMs, createdAtMs);
        next.Deleted = false;
        if (next.NextTurnSequence == 0 && next.Turns.Count == 0)
            next.NextTurnSequence = 1;
        next.Initialization = new ChatConversationInitialization
        {
            OperationId = evt.OperationId,
            ScopeId = evt.ScopeId,
            ConversationId = evt.ConversationId,
            ServiceId = evt.ServiceId,
            ServiceKind = evt.ServiceKind,
            CreatedAt = evt.CreatedAt.Clone(),
            InitialTitle = evt.InitialTitle,
        };
        return next;
    }

    private static ChatConversationState ApplyChatTurnTerminalReconciled(
        ChatConversationState state,
        ChatTurnTerminalReconciledEvent evt)
    {
        if (evt.Turn is null ||
            !string.Equals(state.ScopeId, evt.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(state.ConversationId, evt.ConversationId, StringComparison.Ordinal))
        {
            return state;
        }

        var existingIndex = state.Turns
            .Select((turn, index) => (turn, index))
            .FirstOrDefault(entry => string.Equals(
                entry.turn.TurnId,
                evt.Turn.TurnId,
                StringComparison.Ordinal));
        if (existingIndex.turn is null ||
            existingIndex.turn.TerminalStatus != evt.PreviousStatus ||
            !CanReconcileTerminal(existingIndex.turn, evt.Turn))
        {
            return state;
        }

        var next = state.Clone();
        var reconciledTurn = evt.Turn.Clone();
        reconciledTurn.Sequence = existingIndex.turn.Sequence;
        next.Turns[existingIndex.index] = reconciledTurn;
        next.UpdatedAtMs = reconciledTurn.TerminalTime?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                           ?? next.UpdatedAtMs;
        next.LastRejectedAppend = null;
        return next;
    }

    private static ChatConversationState ApplyChatTurnAppendRejected(
        ChatConversationState state,
        ChatTurnAppendRejectedEvent evt)
    {
        var next = state.Clone();
        next.LastRejectedAppend = new ChatTurnAppendRejection
        {
            TurnId = evt.TurnId,
            Reason = evt.Reason,
            RejectedAt = evt.RejectedAt?.Clone(),
        };
        return next;
    }

    private static ChatConversationState ApplyConversationDeleted(
        ChatConversationState state,
        ConversationDeletedEvent evt)
    {
        var next = state.Clone();
        next.Deleted = true;
        next.ScopeId = string.IsNullOrWhiteSpace(next.ScopeId) ? evt.ScopeId : next.ScopeId;
        next.ConversationId = string.IsNullOrWhiteSpace(next.ConversationId) ? evt.ConversationId : next.ConversationId;
        if (evt.DeletedAt is not null)
            next.UpdatedAtMs = evt.DeletedAt.ToDateTimeOffset().ToUnixTimeMilliseconds();
        return next;
    }
}
