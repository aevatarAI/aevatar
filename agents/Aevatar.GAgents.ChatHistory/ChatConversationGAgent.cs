using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Application.Studio.Abstractions;
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
        if (string.IsNullOrWhiteSpace(evt.ScopeId) ||
            string.IsNullOrWhiteSpace(evt.ConversationId) ||
            string.IsNullOrWhiteSpace(evt.OperationId))
        {
            return;
        }

        var scopeId = evt.ScopeId.Trim();
        var conversationId = evt.ConversationId.Trim();
        var operationId = evt.OperationId.Trim();
        var completionActorId = evt.CompletionActorId?.Trim() ?? string.Empty;
        var ownerKind = ResolveOwnerKind(scopeId, conversationId);
        if (ownerKind == ConversationDeletionOwnerKind.Unspecified)
            return;

        var existingAcknowledgement = FindDeletionAcknowledgement(operationId, ownerKind);
        if (existingAcknowledgement is not null)
        {
            if (MatchesDeletionOperation(
                    existingAcknowledgement,
                    scopeId,
                    conversationId,
                    operationId,
                    completionActorId,
                    ownerKind,
                    Id))
            {
                await DispatchDeletionCommittedAsync(existingAcknowledgement).ConfigureAwait(false);
            }
            return;
        }

        var isPristine = State.Turns.Count == 0 &&
                         string.IsNullOrWhiteSpace(State.ScopeId) &&
                         string.IsNullOrWhiteSpace(State.ConversationId);
        var tupleMatches = string.Equals(State.ScopeId, scopeId, StringComparison.Ordinal) &&
                           string.Equals(State.ConversationId, conversationId, StringComparison.Ordinal);
        if (State.Deleted)
        {
            if (!tupleMatches && ownerKind != ConversationDeletionOwnerKind.Legacy)
                return;

            await PersistDeletionAcknowledgementAsync(
                    scopeId,
                    conversationId,
                    operationId,
                    completionActorId,
                    ownerKind,
                    tupleMatches
                        ? ConversationDeletionAcknowledgementOutcome.AlreadyDeleted
                        : ConversationDeletionAcknowledgementOutcome.AuthoritativeAbsent)
                .ConfigureAwait(false);
            return;
        }

        if (ownerKind == ConversationDeletionOwnerKind.Legacy && (isPristine || !tupleMatches))
        {
            await PersistDeletionAcknowledgementAsync(
                    scopeId,
                    conversationId,
                    operationId,
                    completionActorId,
                    ownerKind,
                    ConversationDeletionAcknowledgementOutcome.AuthoritativeAbsent)
                .ConfigureAwait(false);
            return;
        }

        if (!isPristine && !tupleMatches)
            return;

        var committed = evt.Clone();
        committed.ScopeId = scopeId;
        committed.ConversationId = conversationId;
        committed.OperationId = operationId;
        committed.CompletionActorId = completionActorId;
        committed.CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        committed.OwnerActorId = Id;
        committed.OwnerKind = ownerKind;
        await PersistDomainEventAsync(committed).ConfigureAwait(false);
        await DispatchDeletionCommittedAsync(State.DeletionAcknowledgements[operationId])
            .ConfigureAwait(false);
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
            .On<ConversationDeletionAcknowledgedEvent>(ApplyConversationDeletionAcknowledged)
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
        next.DeletionOperationId = evt.OperationId;
        next.DeletionCompletionActorId = evt.CompletionActorId;
        next.DeletionCommittedAt = evt.CommittedAt?.Clone();
        next.DeletionAcknowledgements[evt.OperationId] = new ConversationDeletionAcknowledgement
        {
            OperationId = evt.OperationId,
            ScopeId = evt.ScopeId,
            ConversationId = evt.ConversationId,
            CompletionActorId = evt.CompletionActorId,
            OwnerActorId = evt.OwnerActorId,
            OwnerKind = evt.OwnerKind,
            Outcome = ConversationDeletionAcknowledgementOutcome.CommittedDeleted,
            CommittedAt = evt.CommittedAt?.Clone(),
        };
        next.UpdatedAtMs = evt.CommittedAt?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? next.UpdatedAtMs;
        return next;
    }

    private static ChatConversationState ApplyConversationDeletionAcknowledged(
        ChatConversationState state,
        ConversationDeletionAcknowledgedEvent evt)
    {
        var next = state.Clone();
        next.DeletionAcknowledgements[evt.OperationId] = new ConversationDeletionAcknowledgement
        {
            OperationId = evt.OperationId,
            ScopeId = evt.ScopeId,
            ConversationId = evt.ConversationId,
            CompletionActorId = evt.CompletionActorId,
            OwnerActorId = evt.OwnerActorId,
            OwnerKind = evt.OwnerKind,
            Outcome = evt.Outcome,
            CommittedAt = evt.CommittedAt?.Clone(),
        };
        return next;
    }

    private ConversationDeletionOwnerKind ResolveOwnerKind(string scopeId, string conversationId)
    {
        if (string.Equals(Id, ChatHistoryActorIds.Conversation(scopeId, conversationId), StringComparison.Ordinal))
            return ConversationDeletionOwnerKind.Canonical;
        if (string.Equals(Id, ChatHistoryActorIds.LegacyConversation(scopeId, conversationId), StringComparison.Ordinal))
            return ConversationDeletionOwnerKind.Legacy;
        return ConversationDeletionOwnerKind.Unspecified;
    }

    private ConversationDeletionAcknowledgement? FindDeletionAcknowledgement(
        string operationId,
        ConversationDeletionOwnerKind ownerKind)
    {
        if (State.DeletionAcknowledgements.TryGetValue(operationId, out var recorded))
        {
            var normalized = recorded.Clone();
            if (string.IsNullOrWhiteSpace(normalized.OwnerActorId))
                normalized.OwnerActorId = Id;
            if (normalized.OwnerKind == ConversationDeletionOwnerKind.Unspecified)
                normalized.OwnerKind = ownerKind;
            return normalized;
        }

        if (!State.Deleted ||
            !string.Equals(State.DeletionOperationId, operationId, StringComparison.Ordinal) ||
            State.DeletionCommittedAt is null)
        {
            return null;
        }

        return new ConversationDeletionAcknowledgement
        {
            OperationId = State.DeletionOperationId,
            ScopeId = State.ScopeId,
            ConversationId = State.ConversationId,
            CompletionActorId = State.DeletionCompletionActorId,
            OwnerActorId = Id,
            OwnerKind = ownerKind,
            Outcome = ConversationDeletionAcknowledgementOutcome.CommittedDeleted,
            CommittedAt = State.DeletionCommittedAt.Clone(),
        };
    }

    private static bool MatchesDeletionOperation(
        ConversationDeletionAcknowledgement acknowledgement,
        string scopeId,
        string conversationId,
        string operationId,
        string completionActorId,
        ConversationDeletionOwnerKind ownerKind,
        string ownerActorId) =>
        string.Equals(acknowledgement.OperationId, operationId, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.ScopeId, scopeId, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.ConversationId, conversationId, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.CompletionActorId, completionActorId, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.OwnerActorId, ownerActorId, StringComparison.Ordinal) &&
        acknowledgement.OwnerKind == ownerKind;

    private async Task PersistDeletionAcknowledgementAsync(
        string scopeId,
        string conversationId,
        string operationId,
        string completionActorId,
        ConversationDeletionOwnerKind ownerKind,
        ConversationDeletionAcknowledgementOutcome outcome)
    {
        await PersistDomainEventAsync(new ConversationDeletionAcknowledgedEvent
        {
            OperationId = operationId,
            ScopeId = scopeId,
            ConversationId = conversationId,
            CompletionActorId = completionActorId,
            OwnerActorId = Id,
            OwnerKind = ownerKind,
            Outcome = outcome,
            CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }).ConfigureAwait(false);
        await DispatchDeletionCommittedAsync(State.DeletionAcknowledgements[operationId])
            .ConfigureAwait(false);
    }

    private async Task DispatchDeletionCommittedAsync(
        ConversationDeletionAcknowledgement acknowledgement)
    {
        if (string.IsNullOrWhiteSpace(acknowledgement.CompletionActorId) ||
            string.IsNullOrWhiteSpace(acknowledgement.OperationId) ||
            acknowledgement.CommittedAt is null)
            return;

        var dispatchPort = Services?.GetService<IActorDispatchPort>();
        if (dispatchPort is null)
            return;

        var completion = new ChatHistoryConversationDeletionCommitted
        {
            OperationId = acknowledgement.OperationId,
            ScopeId = acknowledgement.ScopeId,
            ConversationId = acknowledgement.ConversationId,
            CommittedAt = acknowledgement.CommittedAt.Clone(),
            OwnerActorId = acknowledgement.OwnerActorId,
            OwnerKind = acknowledgement.OwnerKind switch
            {
                ConversationDeletionOwnerKind.Canonical => ChatHistoryConversationOwnerKind.Canonical,
                ConversationDeletionOwnerKind.Legacy => ChatHistoryConversationOwnerKind.Legacy,
                _ => ChatHistoryConversationOwnerKind.Unspecified,
            },
            Outcome = acknowledgement.Outcome switch
            {
                ConversationDeletionAcknowledgementOutcome.CommittedDeleted =>
                    ChatHistoryConversationDeletionOutcome.CommittedDeleted,
                ConversationDeletionAcknowledgementOutcome.AlreadyDeleted =>
                    ChatHistoryConversationDeletionOutcome.AlreadyDeleted,
                ConversationDeletionAcknowledgementOutcome.AuthoritativeAbsent =>
                    ChatHistoryConversationDeletionOutcome.AuthoritativeAbsent,
                _ => ChatHistoryConversationDeletionOutcome.Unspecified,
            },
            CompletionActorId = acknowledgement.CompletionActorId,
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(completion),
            Route = EnvelopeRouteSemantics.CreateDirect(
                Id,
                acknowledgement.CompletionActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = acknowledgement.OperationId,
            },
        };
        await dispatchPort.DispatchAsync(
                acknowledgement.CompletionActorId,
                envelope,
                CancellationToken.None)
            .ConfigureAwait(false);
    }
}
