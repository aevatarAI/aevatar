namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Write-side completion-path port that lets a callback / command handler
/// synchronously wait for a specific event to materialize on a named readmodel
/// before returning to the caller. This is NOT a query-time priming hook
/// (CLAUDE.md prohibits query-time priming on QueryPort/QueryService);
/// it is invoked only on the write-side completion path. See
/// ADR-0017 §Projection Readiness.
/// </summary>
public interface IProjectionReadinessPort
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the projection identified by
    /// <paramref name="readmodelId"/> to advance past the version produced by
    /// the event identified by <paramref name="eventId"/>. Returns when the
    /// watermark has caught up; throws <see cref="TimeoutException"/> when the
    /// wait exceeds <paramref name="timeout"/>.
    /// </summary>
    Task WaitForEventAsync(
        string eventId,
        string readmodelId,
        TimeSpan timeout,
        CancellationToken ct = default);
}
