namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceTrafficViewProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
