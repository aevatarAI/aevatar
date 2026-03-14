namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceEndpointCatalogProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
