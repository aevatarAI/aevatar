namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Narrow actor-side maintenance capability for comparing in-memory state with
/// the authoritative committed stream version. It exposes no events or state
/// payload and is not a query/read-model contract.
/// </summary>
public interface ICommittedEventStreamVersionProbe
{
    Task<long> GetCommittedVersionAsync(CancellationToken ct = default);
}
