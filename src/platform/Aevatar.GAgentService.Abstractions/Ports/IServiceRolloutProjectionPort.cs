namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRolloutProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
