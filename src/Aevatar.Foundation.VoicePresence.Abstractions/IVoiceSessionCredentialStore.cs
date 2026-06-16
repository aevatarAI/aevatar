namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Volatile, in-memory holder of the caller's NyxID access token for an active voice session,
/// keyed by session id. The <c>/ws/voice</c> host stashes the token at attach time (where the
/// caller's bearer is available); the actor reads it back at tool-call time — the voice tool
/// invocation runs on an actor turn that has no caller AsyncLocal context, so the token cannot
/// flow there implicitly.
///
/// The token is NEVER persisted (no grain/proto state, no event store) — it lives only in process
/// memory for the lifetime of the session and is evicted on session end / expiry. This keeps the
/// zero-secret-material invariant (#375 / ADR-0018) while letting voice tools (e.g. nyxid_proxy)
/// authenticate as the caller. Mirrors the host-co-located, per-session lifetime of
/// <see cref="IVoiceVolatileMediaStreamPort" />.
/// </summary>
// Refactor (cluster-voice-tool-caller-credential): voice tool calls cross the actor boundary, so the
// AsyncLocal credential flow that chat relies on (AgentToolContextScope) is unavailable. This store
// carries the caller token across that boundary in volatile memory; the actor re-establishes the
// AsyncLocal scope at tool-call time via VoiceCallerCredentialScope.
public interface IVoiceSessionCredentialStore
{
    /// <summary>Stash the caller's NyxID access token for <paramref name="sessionId" />. No-op on empty input.</summary>
    void Set(string sessionId, string nyxIdAccessToken, DateTimeOffset expiresAtUtc);

    /// <summary>True (and sets <paramref name="nyxIdAccessToken" />) when a non-expired token is held for the session.</summary>
    bool TryGet(string sessionId, out string nyxIdAccessToken);

    /// <summary>Evict the token for <paramref name="sessionId" /> (call on session end).</summary>
    void Remove(string sessionId);
}
