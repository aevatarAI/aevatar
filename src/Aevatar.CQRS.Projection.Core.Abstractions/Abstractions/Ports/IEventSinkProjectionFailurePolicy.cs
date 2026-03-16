using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Failure policy contract for projection runtime forwarding to event sinks.
/// </summary>
public interface IEventSinkProjectionFailurePolicy<TLease, TEvent>
    where TLease : class, IProjectionRuntimeLease
    where TEvent : class
{
    ValueTask<bool> TryHandleAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        Exception exception,
        CancellationToken ct = default);
}
