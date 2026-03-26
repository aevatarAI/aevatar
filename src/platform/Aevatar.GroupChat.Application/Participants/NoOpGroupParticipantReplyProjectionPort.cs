using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Participants;

public sealed class NoOpGroupParticipantReplyProjectionPort : IGroupParticipantReplyProjectionPort
{
    public bool ProjectionEnabled => false;

    public Task EnsureParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReleaseParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}
