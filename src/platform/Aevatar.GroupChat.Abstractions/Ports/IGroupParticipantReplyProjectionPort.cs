namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IGroupParticipantReplyProjectionPort
{
    bool ProjectionEnabled { get; }

    Task EnsureParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default);

    Task ReleaseParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default);
}
