namespace Aevatar.GAgents.ChannelRuntime;

public interface IAgentRegistryQueryPort
{
    Task<AgentRegistryEntry?> GetAsync(string agentId, CancellationToken ct = default);

    Task<long?> GetStateVersionAsync(string agentId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRegistryEntry>> QueryAllAsync(CancellationToken ct = default);
}
