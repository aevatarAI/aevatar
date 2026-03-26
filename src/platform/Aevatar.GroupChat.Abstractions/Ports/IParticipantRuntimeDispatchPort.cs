using Aevatar.GroupChat.Abstractions.Participants;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IParticipantRuntimeDispatchPort
{
    Task<ParticipantRuntimeDispatchResult?> DispatchAsync(
        ParticipantRuntimeDispatchRequest request,
        CancellationToken ct = default);
}
