namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

/// <summary>
/// Recovery surface for a stale/missing <c>VoicePresenceCapabilityReadModel</c> row.
///
/// Re-fires the committed-state projection activation plan for the voice capability actor
/// (the GAgent actor id the capability was enabled on) so the read-model row re-materializes from the
/// actor's already-committed events — WITHOUT committing a new event and WITHOUT mutating authoritative
/// grain state. CQRS read-model-only is preserved: this rebuilds the replica; it does not read the grain
/// as an authority and does not write through the capability command port.
///
/// Motivation: an auto-provisioned default voice agent commits its
/// <c>VoicePresenceRuntimeStateChangedEvent</c> to the grain, but its capability read-model row can be
/// missing/stale (projection lag or a long-idle grain), so the lease-acquire path reads
/// <c>null</c>/stale capability facts and /ws/voice fails closed (404 NotFound, or an empty-body 500 from
/// the lease-observation timeout). The voice ingress uses this port to self-heal that single row before
/// failing closed — so attach works without a manual <c>voice-presence/enable</c>. Mirrors
/// <c>IChatRoutePolicyProjectionRecoveryPort</c>.
/// </summary>
public interface IVoicePresenceCapabilityProjectionRecoveryPort
{
    /// <summary>
    /// Triggers re-materialization of the voice capability projection row for the given actor.
    /// Returns <c>true</c> when a re-materialization was dispatched, <c>false</c> when none could be
    /// (blank actor id, the actor has no committed events, or dispatch failed). A <c>true</c> result does
    /// not guarantee the row is already visible — materialization may complete asynchronously, so callers
    /// should re-read with a bounded window.
    /// </summary>
    Task<bool> TryRematerializeAsync(string actorId, CancellationToken ct = default);
}
