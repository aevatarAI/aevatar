namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IAgentFeedProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
