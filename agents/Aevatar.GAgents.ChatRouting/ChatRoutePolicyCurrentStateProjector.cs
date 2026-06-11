using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Materializes <see cref="ChatRoutePolicyState"/> committed events into
/// <see cref="ChatRoutePolicyCurrentStateDocument"/> in the projection document
/// store. Each committed state is an authoritative full snapshot, so the
/// projector overwrite-writes the whole document — it never reads the previous
/// document to derive the next (CLAUDE.md 覆盖复制优先).
///
/// <c>state_version</c> / <c>last_event_id</c> come from the committed
/// <see cref="StateEvent"/> (the authoritative version watermark); the business
/// fields mirror <see cref="ChatRoutePolicyState"/> 1:1.
/// </summary>
public sealed class ChatRoutePolicyCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ChatRoutePolicyMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ChatRoutePolicyCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ChatRoutePolicyCurrentStateProjector(
        IProjectionWriteDispatcher<ChatRoutePolicyCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ChatRoutePolicyMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ChatRoutePolicyState>(
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

        var document = new ChatRoutePolicyCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            PolicyId = state.PolicyId,
            OwnerScope = state.OwnerScope?.Clone(),
            DefaultTarget = state.DefaultTarget?.Clone(),
            PolicyVersion = state.Version,
            PolicyUpdatedAt = state.UpdatedAt?.Clone(),
        };
        document.Rules.AddRange(state.Rules.Select(rule => rule.Clone()));

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
