using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedQueryPort
{
    Task<AgentFeedSnapshot?> GetFeedAsync(string agentId, CancellationToken ct = default);

    Task<AgentFeedItemSnapshot?> GetTopItemAsync(string agentId, CancellationToken ct = default);
}
