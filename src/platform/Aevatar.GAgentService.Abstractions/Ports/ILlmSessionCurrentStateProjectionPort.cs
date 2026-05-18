namespace Aevatar.GAgentService.Abstractions.Ports;

public interface ILlmSessionCurrentStateProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
