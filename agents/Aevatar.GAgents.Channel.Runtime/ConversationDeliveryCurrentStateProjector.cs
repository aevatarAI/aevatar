using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ConversationDeliveryCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ConversationDeliveryMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ConversationDeliveryCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ConversationDeliveryCurrentStateProjector(
        IProjectionWriteDispatcher<ConversationDeliveryCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ConversationDeliveryMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ConversationGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state is null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        await _writeDispatcher.UpsertAsync(
            Materialize(context, state, stateEvent, updatedAt),
            ct);
    }

    private static ConversationDeliveryCurrentStateDocument Materialize(
        ConversationDeliveryMaterializationContext context,
        ConversationGAgentState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        var document = new ConversationDeliveryCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            Conversation = state.Conversation?.Clone() ?? new ConversationReference(),
            LastSuccessfulDelivery = state.LastSuccessfulDelivery?.Clone(),
        };
        document.RecentDeliveries.AddRange(state.RecentDeliveries.Select(static entry => entry.Clone()));
        return document;
    }
}
