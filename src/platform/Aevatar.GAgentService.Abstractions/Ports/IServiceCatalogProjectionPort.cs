namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceCatalogProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
