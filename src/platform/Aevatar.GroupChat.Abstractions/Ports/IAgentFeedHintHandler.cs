namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedHintHandler
{
    Task HandleAsync(AgentFeedHint hint, CancellationToken ct = default);
}
