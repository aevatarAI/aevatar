using Google.Protobuf;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Shared actor-stream subscription hub that multiplexes one actor stream subscription
/// to many logical handlers.
/// </summary>
public interface IActorStreamSubscriptionHub<TMessage>
    where TMessage : class, IMessage, new()
{
    /// <summary>
    /// Registers one handler for the specified actor and returns a disposable handle.
    /// </summary>
    Task<IAsyncDisposable> RegisterAsync(
        string actorId,
        Func<TMessage, ValueTask> handler,
        CancellationToken ct = default);
}
