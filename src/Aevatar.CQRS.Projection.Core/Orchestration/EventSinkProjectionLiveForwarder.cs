using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projection live sink forwarder for event-sink based delivery.
/// </summary>
public class EventSinkProjectionLiveForwarder<TLease, TEvent>
    : IProjectionPortLiveSinkForwarder<TLease, IEventSink<TEvent>, TEvent>
{
    private readonly IProjectionPortSinkFailurePolicy<TLease, IEventSink<TEvent>, TEvent> _sinkFailurePolicy;

    public EventSinkProjectionLiveForwarder(
        IProjectionPortSinkFailurePolicy<TLease, IEventSink<TEvent>, TEvent> sinkFailurePolicy)
    {
        _sinkFailurePolicy = sinkFailurePolicy ?? throw new ArgumentNullException(nameof(sinkFailurePolicy));
    }

    public async ValueTask ForwardAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();

        try
        {
            await sink.PushAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var handled = await _sinkFailurePolicy.TryHandleAsync(
                lease,
                sink,
                evt,
                ex,
                CancellationToken.None);
            if (!handled)
                throw;
        }
    }
}
