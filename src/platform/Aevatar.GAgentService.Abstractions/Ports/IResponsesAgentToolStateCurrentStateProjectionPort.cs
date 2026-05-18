namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponsesAgentToolStateCurrentStateProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
