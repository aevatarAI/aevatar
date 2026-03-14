namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServicePolicyProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
