namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IRoleCatalogQueryPort
{
    Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default);
}
