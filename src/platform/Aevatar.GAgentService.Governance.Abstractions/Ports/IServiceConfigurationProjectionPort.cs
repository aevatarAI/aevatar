namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceConfigurationProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
