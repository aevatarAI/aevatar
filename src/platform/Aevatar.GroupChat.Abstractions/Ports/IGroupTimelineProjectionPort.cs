namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupTimelineProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
