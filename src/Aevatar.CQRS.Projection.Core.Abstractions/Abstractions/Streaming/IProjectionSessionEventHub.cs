namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Publishes and subscribes projection session events by root-actor/session key.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: hub APIs named the first routing dimension scopeId.
// Refactor (iter367/cluster-issue377): Old pattern: scopeId actually carried the projection root actor id.
// Refactor (iter367/cluster-issue377): New principle: the API names the routing invariant RootActorId.
// Refactor (iter367/cluster-issue377): New principle: session event fanout uses RootActorId + SessionId.
public interface IProjectionSessionEventHub<TEvent>
    where TEvent : class
{
    Task PublishAsync(
        string rootActorId,
        string sessionId,
        TEvent evt,
        CancellationToken ct = default);

    Task<IAsyncDisposable> SubscribeAsync(
        string rootActorId,
        string sessionId,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default);
}
