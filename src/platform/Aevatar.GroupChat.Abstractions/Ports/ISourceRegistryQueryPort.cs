using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface ISourceRegistryQueryPort
{
    Task<GroupSourceCatalogSnapshot?> GetSourceAsync(string sourceId, CancellationToken ct = default);
}
