namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-conversation LLM reply driver. Replaces the host-level inbox stream subscriber with a seam
/// that <see cref="ConversationGAgent"/> calls directly, so each conversation actor owns its own
/// LLM reply work and different conversations run in parallel rather than serializing on a single
/// silo-wide subscription.
/// </summary>
/// <remarks>
/// <para>
/// The actor must persist the request into <c>State.PendingLlmReplyRequests</c> before invoking
/// this seam: the executor runs the LLM call on a background task and only signals completion via
/// dispatch, so durability is provided by actor state, not by the executor.
/// </para>
/// <para>
/// Implementations MUST eventually deliver one terminal signal to <see cref="NeedsLlmReplyEvent.TargetActorId"/>:
/// either an <see cref="LlmReplyReadyEvent"/> (success or classified failure) or a
/// <see cref="DeferredLlmReplyDroppedEvent"/> (request rejected by a pre-LLM gate). Without a
/// terminal signal, the actor's pending entry would leak.
/// </para>
/// <para>
/// <see cref="StartAsync"/> returns once the work has been kicked off — not when the LLM call
/// completes. The actor turn must not block on the LLM call (60-300s) or it would re-introduce the
/// per-actor serial bottleneck this seam exists to remove.
/// </para>
/// </remarks>
public interface IConversationLlmReplyExecutor
{
    Task StartAsync(NeedsLlmReplyEvent request, CancellationToken ct);
}
