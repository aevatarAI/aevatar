namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Stateless port used by <see cref="ConversationGAgent"/> to hand one deferred
/// LLM reply run to its run-scoped continuation owner.
/// </summary>
public interface IChannelLlmReplyRunDispatcher
{
    Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct);
}
