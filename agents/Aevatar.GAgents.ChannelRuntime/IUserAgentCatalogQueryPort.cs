namespace Aevatar.GAgents.ChannelRuntime;

public interface IUserAgentCatalogQueryPort
{
    Task<AgentRegistryEntry?> GetAsync(string agentId, CancellationToken ct = default);

    Task<long?> GetStateVersionAsync(string agentId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRegistryEntry>> QueryAllAsync(CancellationToken ct = default);
}
