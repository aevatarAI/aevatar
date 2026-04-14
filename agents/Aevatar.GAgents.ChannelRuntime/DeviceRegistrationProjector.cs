using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Materializes <see cref="DeviceRegistrationState"/> into per-entry
/// <see cref="DeviceRegistrationDocument"/> documents for query-side read model.
///
/// Known limitation: <see cref="IProjectionWriteDispatcher{T}"/> only supports
/// <c>UpsertAsync</c>. When a device is unregistered, the state no longer contains
/// that entry, but the orphaned document is not deleted. A future
/// <c>IProjectionWriteDispatcher.DeleteAsync</c> is needed to close this gap.
/// Until then, the <see cref="DeviceRegistrationQueryPort"/> should filter by
/// cross-referencing the actor's authoritative state version if strict consistency
/// is required.
/// </summary>
public sealed class DeviceRegistrationProjector
    : ICurrentStateProjectionMaterializer<DeviceRegistrationMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<DeviceRegistrationDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public DeviceRegistrationProjector(
        IProjectionWriteDispatcher<DeviceRegistrationDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        DeviceRegistrationMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<DeviceRegistrationState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        // NOTE: only upserts current entries. Orphaned documents from unregistered
        // devices remain until IProjectionWriteDispatcher gains DeleteAsync support.
        foreach (var entry in state.Registrations)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            var document = new DeviceRegistrationDocument
            {
                Id = entry.Id,
                ScopeId = entry.ScopeId ?? string.Empty,
                HmacKey = entry.HmacKey ?? string.Empty,
                NyxConversationId = entry.NyxConversationId ?? string.Empty,
                Description = entry.Description ?? string.Empty,
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
                ActorId = context.RootActorId,
                UpdatedAt = updatedAt,
            };

            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }
}
