namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Stateless port used by <see cref="ConversationGAgent"/> to hand one deferred
/// LLM reply run to its run-scoped continuation owner.
/// </summary>
/// <remarks>
/// A normal return only promises accepted-for-dispatch per ADR-0021: the run
/// request has been handed to the run actor dispatch port.
/// It does NOT promise the LLM has started, that any reply has been produced,
/// that the actor admitted the run, or that any user-visible delivery has
/// happened. Strong guarantees only arrive via downstream events.
/// </remarks>
public interface IChannelLlmReplyRunDispatcher
{
    Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct);
}
