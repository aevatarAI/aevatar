using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Common sink failure handling template for event-sink based projection live delivery.
/// </summary>
public abstract class EventSinkProjectionFailurePolicyBase<TLease, TEvent>
    : IProjectionPortSinkFailurePolicy<TLease, IEventSink<TEvent>, TEvent>
    where TLease : class
    where TEvent : class
{
    private readonly IProjectionPortSinkSubscriptionManager<TLease, IEventSink<TEvent>, TEvent> _sinkSubscriptionManager;

    protected EventSinkProjectionFailurePolicyBase(
        IProjectionPortSinkSubscriptionManager<TLease, IEventSink<TEvent>, TEvent> sinkSubscriptionManager)
    {
        _sinkSubscriptionManager = sinkSubscriptionManager
            ?? throw new ArgumentNullException(nameof(sinkSubscriptionManager));
    }

    public async ValueTask<bool> TryHandleAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sourceEvent);
        ArgumentNullException.ThrowIfNull(exception);
        ct.ThrowIfCancellationRequested();

        switch (exception)
        {
            case EventSinkBackpressureException backpressureException:
                await DetachSinkAsync(lease, sink);
                await OnBackpressureAsync(lease, sink, sourceEvent, backpressureException, CancellationToken.None);
                return true;
            case EventSinkCompletedException completedException:
                await DetachSinkAsync(lease, sink);
                await OnCompletedAsync(lease, sink, sourceEvent, completedException, CancellationToken.None);
                return true;
            case InvalidOperationException invalidOperationException:
                await DetachSinkAsync(lease, sink);
                await OnInvalidOperationAsync(lease, sink, sourceEvent, invalidOperationException, CancellationToken.None);
                return true;
            default:
                return await OnUnhandledExceptionAsync(lease, sink, sourceEvent, exception, CancellationToken.None);
        }
    }

    protected virtual ValueTask OnBackpressureAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        EventSinkBackpressureException exception,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask OnCompletedAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        EventSinkCompletedException exception,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask OnInvalidOperationAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        InvalidOperationException exception,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask<bool> OnUnhandledExceptionAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        TEvent sourceEvent,
        Exception exception,
        CancellationToken ct) => ValueTask.FromResult(false);

    private Task DetachSinkAsync(TLease lease, IEventSink<TEvent> sink) =>
        _sinkSubscriptionManager.DetachAsync(lease, sink, CancellationToken.None);
}
