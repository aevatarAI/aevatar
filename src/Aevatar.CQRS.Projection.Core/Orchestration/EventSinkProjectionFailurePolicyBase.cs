using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Common sink failure handling template for event-sink based projection live delivery.
/// </summary>
public abstract class EventSinkProjectionFailurePolicyBase<TLease, TEvent>
    : IEventSinkProjectionFailurePolicy<TLease, TEvent>
    where TLease : class, IProjectionRuntimeLease
    where TEvent : class
{
    private readonly IEventSinkProjectionSubscriptionManager<TLease, TEvent> _sinkSubscriptionManager;

    protected EventSinkProjectionFailurePolicyBase(
        IEventSinkProjectionSubscriptionManager<TLease, TEvent> sinkSubscriptionManager)
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
                try
                {
                    await OnBackpressureAsync(lease, sink, sourceEvent, backpressureException, CancellationToken.None);
                }
                finally
                {
                    TryCompleteCurrentSink(sink);
                }
                return true;
            case EventSinkCompletedException completedException:
                await DetachSinkAsync(lease, sink);
                try
                {
                    await OnCompletedAsync(lease, sink, sourceEvent, completedException, CancellationToken.None);
                }
                finally
                {
                    TryCompleteCurrentSink(sink);
                }
                return true;
            case InvalidOperationException invalidOperationException:
                await DetachSinkAsync(lease, sink);
                try
                {
                    await OnInvalidOperationAsync(lease, sink, sourceEvent, invalidOperationException, CancellationToken.None);
                }
                finally
                {
                    TryCompleteCurrentSink(sink);
                }
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

    private static void TryCompleteCurrentSink(IEventSink<TEvent> sink)
    {
        try
        {
            sink.Complete();
        }
        catch
        {
            // Completing the current sink is best-effort cleanup; detachment already happened.
        }
    }

    private Task DetachSinkAsync(TLease lease, IEventSink<TEvent> sink) =>
        _sinkSubscriptionManager.DetachAsync(lease, sink, CancellationToken.None);
}
