using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

/// <summary>
/// Re-materializes a stale/missing <c>VoicePresenceCapabilityReadModel</c> by re-firing the SAME
/// committed-state activation plan a write fires (see
/// <see cref="VoicePresenceCommittedStateProjectionActivationPlanProvider"/>) — but for a known actor id
/// and without committing a new event. Used by the voice lease-acquire path to self-heal projection
/// staleness (projection lag / long-idle grain) without touching the read-model-only query path or the
/// write-only capability command port. Mirrors <c>ChatRoutePolicyProjectionRecoveryPort</c>.
/// </summary>
internal sealed class VoicePresenceCapabilityProjectionRecoveryPort
    : IVoicePresenceCapabilityProjectionRecoveryPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly ProjectionActivationPlanDispatcher _dispatcher;
    private readonly ILogger<VoicePresenceCapabilityProjectionRecoveryPort> _logger;

    public VoicePresenceCapabilityProjectionRecoveryPort(
        IActorRuntime actorRuntime,
        ProjectionActivationPlanDispatcher dispatcher,
        ILogger<VoicePresenceCapabilityProjectionRecoveryPort>? logger = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger<VoicePresenceCapabilityProjectionRecoveryPort>.Instance;
    }

    public async Task<bool> TryRematerializeAsync(string actorId, CancellationToken ct = default)
    {
        // The capability projection is keyed on the GAgent actor id the capability was enabled on
        // (VoicePresenceCapabilityCommandPort dispatches directly to that actor; the plan provider keys
        // RootActorId = context.ActorId). Without it there is no actor to re-materialize on this path.
        var normalizedActorId = actorId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedActorId))
            return false;

        try
        {
            // Only re-materialize capabilities that ACTUALLY EXIST. The materialization dispatch durably
            // creates a projection-scope grain for the actor; firing it for an actor that never enabled
            // voice presence (no committed VoicePresenceRuntimeStateChangedEvent) would leave an empty,
            // useless scope grain behind. Gate on the same cheap, non-activating existence check the
            // projection runtime uses. This never creates the source grain — ExistsAsync is a read; the
            // heal only re-projects already-committed events (no new commit, no grain mutation, no version
            // bump).
            if (!await _actorRuntime.ExistsAsync(normalizedActorId))
                return false;

            // Identical to the plan VoicePresenceCommittedStateProjectionActivationPlanProvider yields on a
            // commit, re-fired for the known actor id. It re-projects from already-committed events: no new
            // commit, no grain mutation, no State.Version bump.
            await _dispatcher.DispatchAsync(
                new ProjectionActivationPlan
                {
                    LeaseType = typeof(VoicePresenceCapabilityMaterializationRuntimeLease),
                    StartRequest = new ProjectionScopeStartRequest
                    {
                        RootActorId = normalizedActorId,
                        ProjectionKind = VoicePresenceProjectionKinds.CapabilityMaterialization,
                        Mode = ProjectionRuntimeMode.DurableMaterialization,
                    },
                },
                ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "voice-presence capability projection re-materialize failed for actor {ActorId}", normalizedActorId);
            return false;
        }
    }
}
