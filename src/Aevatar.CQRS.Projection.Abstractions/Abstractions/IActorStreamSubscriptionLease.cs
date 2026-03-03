namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Lease handle for one actor stream subscription.
/// </summary>
public interface IActorStreamSubscriptionLease : IAsyncDisposable
{
    string ActorId { get; }
}
