namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceDeploymentCatalogProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
