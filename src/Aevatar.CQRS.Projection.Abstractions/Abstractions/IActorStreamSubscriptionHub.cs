using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Shared actor-stream subscription hub that creates per-handler subscriptions
/// and returns explicit unsubscribe leases.
/// </summary>
public interface IActorStreamSubscriptionHub<TMessage>
    where TMessage : class, IMessage, new()
{
    /// <summary>
    /// Subscribes one handler for the specified actor stream.
    /// Returns a lease that must be disposed to detach the handler.
    /// </summary>
    Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<TMessage, ValueTask> handler,
        CancellationToken ct = default);
}
