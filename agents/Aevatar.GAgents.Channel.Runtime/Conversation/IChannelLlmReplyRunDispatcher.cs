namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Stateless port used by <see cref="ConversationGAgent"/> to hand one deferred
/// LLM reply run to its run-scoped continuation owner.
/// </summary>
/// <remarks>
/// The synchronous return only promises <c>accepted</c> per ADR-0021: the run
/// request has been validated as fresh and enqueued onto the run actor's inbox.
/// It does NOT promise the LLM has started, that any reply has been produced,
/// or that any user-visible delivery has happened. Strong guarantees only
/// arrive via downstream events.
/// </remarks>
public interface IChannelLlmReplyRunDispatcher
{
    Task<DispatchOutcome> DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct);
}

/// <summary>
/// Synchronous outcome of <see cref="IChannelLlmReplyRunDispatcher.DispatchAsync"/>.
/// </summary>
/// <param name="Phase">
/// The completion phase actually reached. By contract dispatcher implementations
/// MUST only return <see cref="DispatchPhase.Accepted"/> or one of the
/// <c>Rejected*</c> variants — never <c>Committed</c> or <c>Delivered</c>; those
/// strong phases are observed asynchronously per ADR-0021.
/// </param>
/// <param name="CommandId">
/// Stable id of the dispatched command (run actor envelope id). Empty when the
/// outcome is a rejection that occurred before envelope construction.
/// </param>
/// <param name="RunActorId">
/// Id of the target <c>AgentRunGAgent</c> the request was routed to, when
/// available; <c>null</c> when no actor was created (e.g. stale-rejected).
/// </param>
/// <param name="AcceptedAtUnixMs">
/// Wall-clock at which the dispatcher accepted/rejected the request. Zero when
/// not applicable.
/// </param>
public sealed record DispatchOutcome(
    DispatchPhase Phase,
    string CommandId,
    string? RunActorId,
    long AcceptedAtUnixMs);

/// <summary>
/// Phase reached by <see cref="IChannelLlmReplyRunDispatcher.DispatchAsync"/>.
/// </summary>
/// <remarks>
/// Per ADR-0021 the dispatcher is only allowed to report <c>Accepted</c> or one
/// of the <c>Rejected*</c> variants. Stronger phases (committed, delivered,
/// finalized) are not observable at the synchronous dispatcher boundary.
/// </remarks>
public enum DispatchPhase
{
    Accepted = 0,
    /// <summary>
    /// The request's <c>requested_at_unix_ms</c> exceeded the freshness window,
    /// so the dispatcher refused to enqueue it (the run actor would have
    /// dropped it anyway).
    /// </summary>
    RejectedStale = 1,
    /// <summary>
    /// The request's <c>correlation_id</c> matches an already-dispatched run
    /// command and was suppressed to keep the run actor inbox idempotent.
    /// </summary>
    RejectedDuplicate = 2,
}
