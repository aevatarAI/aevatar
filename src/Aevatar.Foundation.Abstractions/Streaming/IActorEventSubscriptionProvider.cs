using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Host-side actor event subscription abstraction.
/// Separates read/observe concerns from runtime publish transports.
/// </summary>
public interface IActorEventSubscriptionProvider
{
    Task<IAsyncDisposable> SubscribeAsync<TMessage>(
        string actorId,
        Func<TMessage, Task> handler,
        CancellationToken ct = default)
        where TMessage : class, IMessage, new();
}
