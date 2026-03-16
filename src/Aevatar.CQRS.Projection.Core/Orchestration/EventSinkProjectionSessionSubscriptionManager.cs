using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic sink subscription manager for projection session event streams.
/// </summary>
public class EventSinkProjectionSessionSubscriptionManager<TLease, TEvent>
    : IEventSinkProjectionSubscriptionManager<TLease, TEvent>
    where TLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease
    where TEvent : class
{
    private readonly IProjectionSessionEventHub<TEvent> _sessionEventHub;

    public EventSinkProjectionSessionSubscriptionManager(IProjectionSessionEventHub<TEvent> sessionEventHub)
    {
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public async Task AttachOrReplaceAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = await _sessionEventHub.SubscribeAsync(
            lease.ScopeId,
            lease.SessionId,
            handler,
            ct);

        var previous = lease.AttachOrReplaceLiveSinkSubscription(sink, streamSubscription);
        if (previous != null)
            await previous.DisposeAsync();
    }

    public async Task DetachAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = lease.DetachLiveSinkSubscription(sink);
        if (streamSubscription != null)
            await streamSubscription.DisposeAsync();
    }
}
