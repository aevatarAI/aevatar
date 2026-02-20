using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Shared actor-stream subscription hub that multiplexes one actor stream subscription
/// to many logical handlers.
/// </summary>
public interface IActorStreamSubscriptionHub<TMessage>
    where TMessage : class, IMessage, new()
{
    /// <summary>
    /// Registers one keyed handler for the specified actor.
    /// </summary>
    Task RegisterAsync(
        string actorId,
        string key,
        Func<TMessage, ValueTask> handler,
        CancellationToken ct = default);

    /// <summary>
    /// Unregisters one keyed handler for the specified actor.
    /// </summary>
    Task UnregisterAsync(
        string actorId,
        string key,
        CancellationToken ct = default);
}
