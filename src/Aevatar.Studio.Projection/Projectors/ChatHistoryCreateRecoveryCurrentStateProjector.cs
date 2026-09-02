using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ChatHistoryCreateRecoveryCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ChatHistoryCreateRecoveryCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ChatHistoryCreateRecoveryCurrentStateProjector(
        IProjectionWriteDispatcher<ChatHistoryCreateRecoveryCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<ChatTurnHistoryDeliveryState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null ||
            !state.ExposeCreateRecovery ||
            string.IsNullOrWhiteSpace(state.ScopeId) ||
            string.IsNullOrWhiteSpace(state.SourceCommandId))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var normalizedScopeId = state.ScopeId.Trim();
        var normalizedCommandId = state.SourceCommandId.Trim();
        var document = new ChatHistoryCreateRecoveryCurrentStateDocument
        {
            Id = ChatHistoryCreateRecoveryIds.FromScopeAndCommandId(normalizedScopeId, normalizedCommandId),
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            ScopeId = normalizedScopeId,
            ConversationId = state.ConversationId,
            TurnId = state.TurnId,
            WorkflowActorId = state.SourceActorId,
            WorkflowCommandId = normalizedCommandId,
            WorkflowCorrelationId = state.SourceCorrelationId,
            RequestFingerprint = state.RequestFingerprint,
            Status = ToStatusName(state.Status),
            ReservedAtUnixMs = state.ReservedAtUnixMs,
            CompletedAtUnixMs = state.CompletedAtUnixMs,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static string ToStatusName(ChatTurnHistoryDeliveryStatus status) =>
        status switch
        {
            ChatTurnHistoryDeliveryStatus.Reserved => "reserved",
            ChatTurnHistoryDeliveryStatus.Bound => "bound",
            ChatTurnHistoryDeliveryStatus.AppendDispatched => "append_dispatched",
            ChatTurnHistoryDeliveryStatus.Abandoned => "abandoned",
            ChatTurnHistoryDeliveryStatus.Failed => "failed",
            ChatTurnHistoryDeliveryStatus.AppendCommitted => "append_committed",
            ChatTurnHistoryDeliveryStatus.AppendRejected => "append_rejected",
            ChatTurnHistoryDeliveryStatus.TerminalReconciliationPrepared => "terminal_reconciliation_prepared",
            _ => string.Empty,
        };
}
