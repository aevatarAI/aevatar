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
/// Actor ID: <c>chat-{scopeId}-{conversationId}</c>.
/// </summary>
[GAgent("chat.history.conversation")]
public sealed class ChatConversationGAgent : GAgentBase<ChatConversationState>, IProjectedActor
{
    public static string ProjectionKind => "chat-conversation";

    public const int MaxTurns = 250;
    private const int MaxSynthesizedConversationTitleLength = 48;
    private const string TitleEllipsis = "…";

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
            if (!HasSamePayload(existing, turn))
            {
                await PersistRejectionAsync(command, ChatTurnAppendRejectionReason.Conflict);
                await DispatchAppendResultAsync(command, false, ChatTurnAppendRejectionReason.Conflict);
            }
            else
            {
                await DispatchAppendResultAsync(command, true, ChatTurnAppendRejectionReason.Unspecified);
            }
            return;
        }

        if (State.Turns.Count >= MaxTurns)
        {
            await PersistRejectionAsync(command, ChatTurnAppendRejectionReason.MaxTurnsExceeded);
            await DispatchAppendResultAsync(command, false, ChatTurnAppendRejectionReason.MaxTurnsExceeded);
            return;
        }

        turn.Sequence = State.Turns.Count + 1;
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
    public async Task HandleConversationDeleted(ConversationDeletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ConversationId))
            return;

        if (State.Deleted || (string.IsNullOrWhiteSpace(State.ConversationId) && State.Turns.Count == 0))
            return;

        await PersistDomainEventAsync(evt);
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
            .On<ChatTurnAppendedEvent>(ApplyChatTurnAppended)
            .On<ChatTurnAppendRejectedEvent>(ApplyChatTurnAppendRejected)
            .On<ConversationDeletedEvent>(ApplyConversationDeleted)
            .OrCurrent();
    }

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
            next.Turns.Add(evt.Turn.Clone());
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
        next.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return next;
    }
}
