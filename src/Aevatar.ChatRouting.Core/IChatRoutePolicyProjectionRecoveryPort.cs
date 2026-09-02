using Aevatar.Foundation.Abstractions;

namespace Aevatar.ChatRouting.Core;

/// <summary>
/// Recovery surface for a stale/missing <c>ChatRoutePolicyCurrentStateDocument</c> read-model row.
///
/// Re-fires the committed-state projection activation plan for the caller's policy actor
/// (<c>chat-route-policy:{scopeId}</c>) so the read-model row re-materializes from the actor's
/// already-committed events — WITHOUT committing a new event and WITHOUT mutating authoritative grain
/// state. CQRS read-model-only is preserved: this rebuilds the replica; it does not read the grain as an
/// authority and does not write through the command port.
///
/// Motivation: after the chat-route policy grain sits idle for a long time, its ES projection row can go
/// missing while the grain state stays intact, so <see cref="IChatRoutePolicyQueryPort.LookupForCallerAsync"/>
/// returns null and the voice resolver falls to the text default → 501 on /ws/voice. The voice ingress uses
/// this port to self-heal that single stale row before failing closed.
/// </summary>
public interface IChatRoutePolicyProjectionRecoveryPort
{
    /// <summary>
    /// Triggers re-materialization of the caller's chat-route policy projection row.
    /// Returns <c>true</c> when a re-materialization was dispatched, <c>false</c> when none could be
    /// (e.g. the caller scope carries no id to key the policy actor on). A <c>true</c> result does not
    /// guarantee the row is already visible — materialization may complete asynchronously, so callers
    /// should re-read with a bounded window.
    /// </summary>
    Task<bool> TryRematerializeAsync(OwnerScope callerScope, CancellationToken ct = default);
}
