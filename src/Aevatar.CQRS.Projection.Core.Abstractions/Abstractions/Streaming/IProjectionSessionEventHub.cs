namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Publishes and subscribes projection session events by scope/session key.
/// </summary>
public interface IProjectionSessionEventHub<TEvent>
    where TEvent : class
{
    Task PublishAsync(
        string scopeId,
        string sessionId,
        TEvent evt,
        CancellationToken ct = default);

    Task<IAsyncDisposable> SubscribeAsync(
        string scopeId,
        string sessionId,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default);
}
