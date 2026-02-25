namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic forwarder contract for projection runtime events to external sinks.
/// </summary>
public interface IProjectionPortLiveSinkForwarder<TLease, TSink, TEvent>
{
    ValueTask ForwardAsync(
        TLease lease,
        TSink sink,
        TEvent evt,
        CancellationToken ct = default);
}
