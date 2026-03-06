using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default sink failure policy that applies detach behavior only.
/// </summary>
public sealed class DefaultEventSinkProjectionFailurePolicy<TLease, TEvent>
    : EventSinkProjectionFailurePolicyBase<TLease, TEvent>
    where TLease : class
    where TEvent : class
{
    public DefaultEventSinkProjectionFailurePolicy(
        IEventSinkProjectionSubscriptionManager<TLease, TEvent> sinkSubscriptionManager)
        : base(sinkSubscriptionManager)
    {
    }
}
