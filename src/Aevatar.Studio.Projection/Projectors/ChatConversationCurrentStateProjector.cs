using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="ChatConversationState"/> committed events into
/// <see cref="ChatConversationCurrentStateDocument"/> in the projection document store.
/// </summary>
public sealed class ChatConversationCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ChatConversationCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ChatConversationCurrentStateProjector(
        IProjectionWriteDispatcher<ChatConversationCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ChatConversationState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new ChatConversationCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            ScopeId = state.ScopeId,
            ConversationId = state.ConversationId,
            Title = state.Title,
            ServiceId = state.ServiceId,
            ServiceKind = state.ServiceKind,
            CreatedAtMs = state.CreatedAtMs,
            UpdatedAtMs = state.UpdatedAtMs,
            MessageCount = state.Turns.Count,
            LlmRoute = state.Turns.LastOrDefault()?.LlmRoute ?? string.Empty,
            LlmModel = state.Turns.LastOrDefault()?.LlmModel ?? string.Empty,
            Deleted = state.Deleted,
        };
        document.Turns.AddRange(state.Turns.Select(ToTurnDocument));

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static ChatConversationTurnDocument ToTurnDocument(ChatTurn turn) =>
        new()
        {
            TurnId = turn.TurnId,
            Sequence = turn.Sequence,
            UserText = turn.UserText,
            AssistantText = turn.AssistantText,
            TerminalStatus = ToTerminalStatusName(turn.TerminalStatus),
            SanitizedError = turn.SanitizedError,
            TerminalTimeMs = turn.TerminalTime?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0,
            LlmRoute = turn.LlmRoute,
            LlmModel = turn.LlmModel,
        };

    private static string ToTerminalStatusName(ChatTurnTerminalStatus status) =>
        status switch
        {
            ChatTurnTerminalStatus.Completed => "complete",
            ChatTurnTerminalStatus.Failed => "error",
            ChatTurnTerminalStatus.Stopped => "stopped",
            ChatTurnTerminalStatus.Blocked => "blocked",
            ChatTurnTerminalStatus.OutcomeUncertain => "outcome_uncertain",
            _ => string.Empty,
        };
}
