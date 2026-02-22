namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Projection context that carries its stream subscription lease.
/// </summary>
public interface IProjectionStreamSubscriptionContext
{
    IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
