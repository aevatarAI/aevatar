using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Runtime-owned actor stream subscription handle for one projection session.
/// </summary>
public interface IProjectionStreamSubscriptionRuntimeLease
{
    IActorStreamSubscriptionLease? ActorStreamSubscriptionLease { get; set; }
}
