namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic policy contract for handling sink forwarding failures.
/// </summary>
public interface IProjectionPortSinkFailurePolicy<TLease, TSink, TEvent>
{
    ValueTask<bool> TryHandleAsync(
        TLease lease,
        TSink sink,
        TEvent sourceEvent,
        Exception exception,
        CancellationToken ct = default);
}
