namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceServingSetProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
