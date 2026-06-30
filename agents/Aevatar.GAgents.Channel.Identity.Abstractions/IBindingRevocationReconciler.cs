using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Reconciles the local NyxID binding when a caller observes that the upstream
/// grant is gone (NyxID reports <c>invalid_grant</c> on token-exchange, surfaced
/// as <see cref="BindingRevokedException"/>). Implementations event-source the
/// owning <c>ExternalIdentityBindingGAgent</c> to flip its projection to
/// inactive — they MUST NOT call <see cref="INyxIdCapabilityBroker.RevokeBindingAsync"/>,
/// because the grant is already revoked on the NyxID side; re-revoking it would
/// be redundant and could surface a spurious upstream error.
///
/// This is the self-heal seam the binding lifecycle already anticipates
/// (ADR-0018: "observed invalid_grant ... self-heal"; the proto reserves the
/// <c>nyx_invalid_grant</c> revoke reason). After reconciliation the readmodel
/// reports no active binding, so <c>/whoami</c> shows "未绑定" and <c>/init</c>
/// allows the sender to re-authorize — no <c>/whoami</c> / <c>/init</c> handler
/// changes are needed.
/// </summary>
public interface IBindingRevocationReconciler
{
    /// <summary>
    /// Event-sources a local revoke for <paramref name="subject"/> with the
    /// given audit <paramref name="reason"/>. Idempotent: the owning actor
    /// leaves facts unchanged when no active binding exists. Implementations
    /// SHOULD be safe to call as best-effort fire-and-forget off the reply
    /// path (never block the response on this).
    /// </summary>
    Task ReconcileRevokedAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default);
}
