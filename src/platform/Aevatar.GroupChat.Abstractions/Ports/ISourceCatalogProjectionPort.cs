namespace Aevatar.GroupChat.Abstractions.Ports;

public interface ISourceCatalogProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
