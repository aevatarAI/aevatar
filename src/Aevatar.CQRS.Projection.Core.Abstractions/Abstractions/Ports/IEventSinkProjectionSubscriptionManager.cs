using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Subscription manager for projection live-stream delivery to event sinks.
/// </summary>
public interface IEventSinkProjectionSubscriptionManager<TLease, TEvent>
{
    Task AttachOrReplaceAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default);

    Task DetachAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default);
}
