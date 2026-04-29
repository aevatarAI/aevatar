using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Write-side completion-path port that lets a callback / command handler
/// synchronously wait for the binding readmodel to reflect the expected state
/// before returning to the caller. This is NOT a query-time priming hook
/// (CLAUDE.md prohibits query-time priming on QueryPort/QueryService); it is
/// invoked only on the write-side completion path. See
/// ADR-0018 §Projection Readiness.
/// </summary>
/// <remarks>
/// The interface intentionally describes the binding semantics directly
/// rather than the event-sourcing primitives (event ids / versions) — those
/// primitives are infrastructure-level details produced by
/// <c>PersistDomainEventAsync</c> that the callback handler does not have a
/// reliable way to observe before publishing. Polling the readmodel for the
/// expected binding state matches the real success criterion the callback
/// needs to acknowledge.
/// </remarks>
public interface IProjectionReadinessPort
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the binding document for
    /// <paramref name="externalSubject"/> to report the expected state.
    /// When <paramref name="expectedBindingId"/> is non-null, waits until the
    /// document reports an active binding with that id; when null, waits
    /// until the document reports no active binding (post-revoke).
    /// Throws <see cref="TimeoutException"/> when the wait elapses.
    /// </summary>
    Task WaitForBindingStateAsync(
        ExternalSubjectRef externalSubject,
        string? expectedBindingId,
        TimeSpan timeout,
        CancellationToken ct = default);
}
