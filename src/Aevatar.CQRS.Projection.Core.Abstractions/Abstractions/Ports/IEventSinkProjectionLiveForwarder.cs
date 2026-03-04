using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Event-sink specialization of projection live sink forwarder.
/// </summary>
public interface IEventSinkProjectionLiveForwarder<TLease, TEvent>
    : IProjectionPortLiveSinkForwarder<TLease, IEventSink<TEvent>, TEvent>
{
}
