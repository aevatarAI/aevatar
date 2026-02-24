namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic sink subscription manager for projection live-stream delivery.
/// </summary>
public interface IProjectionPortSinkSubscriptionManager<TLease, TSink, TEvent>
{
    Task AttachOrReplaceAsync(
        TLease lease,
        TSink sink,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default);

    Task DetachAsync(
        TLease lease,
        TSink sink,
        CancellationToken ct = default);
}
