namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

/// <summary>
/// Auto-provisioning surface for the zero-config <c>/ws/voice</c> attach path: when the resolved
/// default voice agent has NO enabled voice capability (its grain never received a
/// <c>VoicePresenceEnableRequested</c>, so the read model is genuinely null — not merely stale),
/// commit an enable for it so first-connect "just works" without a manual <c>voice-presence/enable</c>.
///
/// This is distinct from <see cref="IVoicePresenceCapabilityProjectionRecoveryPort"/>: the recovery port
/// only RE-PROJECTS an already-committed capability (no new event); this port COMMITS a brand-new enable
/// for a never-enabled default agent. The recovery port cannot provision what was never enabled.
///
/// The implementation reuses the SAME capability enable command the <c>voice-presence/enable</c> endpoint
/// uses (<c>IVoicePresenceCapabilityCommandPort.EnableAsync</c>). Because that command dispatches an
/// envelope directly to the target actor, the implementation must resolve and validate the actor's
/// runtime agent kind (via the actor-kind probe + agent-kind registry — mirroring the admin endpoint's
/// agentKind validation) before committing, and must be idempotent / scoped to the zero-config default
/// path: it only enables an actor that actually exists and whose kind is a known, registered agent kind.
/// </summary>
public interface IVoicePresenceCapabilityAutoEnablePort
{
    /// <summary>
    /// Commits an enable for the given actor's voice capability on <paramref name="moduleName"/> when the
    /// actor exists and its runtime agent kind is registered (so the dispatched enable will be accepted).
    /// Returns <c>true</c> when an enable was dispatched, <c>false</c> when none could be (blank actor id,
    /// the actor does not exist, no resolvable/registered runtime kind, or dispatch failed). A <c>true</c>
    /// result does not guarantee the capability read model is already visible — materialization may
    /// complete asynchronously, so callers should re-read with a bounded window.
    /// </summary>
    Task<bool> TryAutoEnableAsync(string actorId, string? moduleName, CancellationToken ct = default);
}
