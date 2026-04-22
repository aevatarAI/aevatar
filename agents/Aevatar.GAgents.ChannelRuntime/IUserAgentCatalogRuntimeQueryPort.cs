namespace Aevatar.GAgents.ChannelRuntime;

public interface IUserAgentCatalogRuntimeQueryPort
{
    Task<UserAgentCatalogEntry?> GetAsync(string agentId, CancellationToken ct = default);

    Task<long?> GetStateVersionAsync(string agentId, CancellationToken ct = default);

    Task<IReadOnlyList<UserAgentCatalogEntry>> QueryAllAsync(CancellationToken ct = default);
}
