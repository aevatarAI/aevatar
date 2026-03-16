namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRevisionCatalogProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
