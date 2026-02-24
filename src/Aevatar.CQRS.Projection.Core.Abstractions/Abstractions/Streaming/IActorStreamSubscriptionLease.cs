namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Lease handle for one actor stream subscription.
/// </summary>
public interface IActorStreamSubscriptionLease : IAsyncDisposable
{
    string ActorId { get; }
}
