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
/// Materializes idempotent chat create delivery state into a scope-bound
/// recovery read model.
/// </summary>
public sealed class ChatCreateRecoveryCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ChatCreateRecoveryCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ChatCreateRecoveryCurrentStateProjector(
        IProjectionWriteDispatcher<ChatCreateRecoveryCurrentStateDocument> writeDispatcher,
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
            string.IsNullOrWhiteSpace(state.CreateIdempotencyKey))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new ChatCreateRecoveryCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            ScopeId = state.ScopeId,
            CreateIdempotencyKey = state.CreateIdempotencyKey,
            CreateRequestHash = state.CreateRequestHash,
            ConversationId = state.ConversationId,
            TurnId = state.TurnId,
            Status = ToStatusName(state.Status),
            SourceVersion = stateEvent.Version,
            DeliveryActorId = context.RootActorId,
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
            _ => string.Empty,
        };
}
