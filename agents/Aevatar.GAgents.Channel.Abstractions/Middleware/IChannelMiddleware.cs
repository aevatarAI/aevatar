namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one narrow turn-scoped middleware in the channel inbound pipeline.
/// </summary>
public interface IChannelMiddleware
{
    /// <summary>
    /// Invokes the middleware and optionally forwards control to the next component.
    /// </summary>
    Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct);
}
