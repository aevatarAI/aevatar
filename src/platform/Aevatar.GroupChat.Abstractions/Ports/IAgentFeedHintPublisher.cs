namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedHintPublisher
{
    Task PublishAsync(AgentFeedHint hint, CancellationToken ct = default);
}
