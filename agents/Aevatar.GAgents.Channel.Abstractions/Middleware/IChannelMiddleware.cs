namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one narrow turn-scoped middleware in the channel inbound pipeline.
/// </summary>
/// <remarks>
/// Middleware runs after the adapter has constructed a <see cref="ChatActivity"/> and before bot logic executes. It is not
/// responsible for ingress signature verification, durable deduplication, or adapter-owned outbound retry behavior.
/// </remarks>
public interface IChannelMiddleware
{
    /// <summary>
    /// Invokes the middleware and optionally forwards control to the next component.
    /// </summary>
    /// <param name="context">The current turn context.</param>
    /// <param name="next">The next middleware or bot delegate in the pipeline.</param>
    /// <param name="ct">A token that cancels pipeline execution.</param>
    /// <returns>A task that completes when the middleware and downstream components have finished.</returns>
    Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct);
}
