namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IRoleCatalogCommandPort
{
    Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task DeleteRoleDraftAsync(
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}
