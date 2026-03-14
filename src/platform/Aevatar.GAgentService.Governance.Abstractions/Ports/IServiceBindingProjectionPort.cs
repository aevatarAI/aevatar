namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceBindingProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
