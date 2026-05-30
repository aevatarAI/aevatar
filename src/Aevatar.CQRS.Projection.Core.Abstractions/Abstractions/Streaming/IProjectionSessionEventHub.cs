namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Publishes and subscribes projection session events by root-actor/session key.
/// </summary>
// Refactor (issue-377): Old pattern: hub APIs named the first routing dimension scopeId.
// Refactor (issue-377): Old pattern: scopeId actually carried the projection root actor id.
// Refactor (issue-377): New principle: the API names the routing invariant RootActorId.
// Refactor (issue-377): New principle: session event fanout uses RootActorId + SessionId.
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
