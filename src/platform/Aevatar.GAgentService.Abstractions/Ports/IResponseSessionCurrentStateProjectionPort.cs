namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponseSessionCurrentStateProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
