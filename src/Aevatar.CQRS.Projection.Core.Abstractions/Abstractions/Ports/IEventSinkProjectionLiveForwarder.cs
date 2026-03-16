using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Live forwarder for projection runtime events to event sinks.
/// </summary>
public interface IEventSinkProjectionLiveForwarder<TLease, TEvent>
{
    ValueTask ForwardAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent evt,
        CancellationToken ct = default);
}
