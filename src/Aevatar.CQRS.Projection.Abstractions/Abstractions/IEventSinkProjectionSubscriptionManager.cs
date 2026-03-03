using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Event-sink specialization of projection sink subscription manager.
/// </summary>
public interface IEventSinkProjectionSubscriptionManager<TLease, TEvent>
    : IProjectionPortSinkSubscriptionManager<TLease, IEventSink<TEvent>, TEvent>
{
}
