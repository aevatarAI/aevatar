using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: VoicePresenceCapabilityReadModel had a mapper shape but no production projection writer.
//   New principle: committed VoicePresenceRuntimeStateChangedEvent facts materialize capability read models in the Projection Pipeline.
public sealed class VoicePresenceCapabilityReadModelProjector
    : ICurrentStateProjectionMaterializer<VoicePresenceCapabilityMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<VoicePresenceCapabilityReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public VoicePresenceCapabilityReadModelProjector(
        IProjectionWriteDispatcher<VoicePresenceCapabilityReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        VoicePresenceCapabilityMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var payload,
                out var eventId,
                out var stateVersion) ||
            payload?.Is(VoicePresenceRuntimeStateChangedEvent.Descriptor) != true)
        {
            return;
        }

        var changed = payload.Unpack<VoicePresenceRuntimeStateChangedEvent>();
        if (string.IsNullOrWhiteSpace(changed.ModuleName) || changed.State == null)
            return;

        var document = VoicePresenceCapabilityReadModelMapper.FromRuntimeState(
            context.RootActorId,
            changed.ModuleName,
            changed.State,
            stateVersion,
            eventId,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow));

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
