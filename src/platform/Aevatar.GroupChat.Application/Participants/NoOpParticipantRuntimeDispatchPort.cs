using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Participants;

public sealed class NoOpParticipantRuntimeDispatchPort : IParticipantRuntimeDispatchPort
{
    public Task<ParticipantRuntimeDispatchResult?> DispatchAsync(ParticipantRuntimeDispatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<ParticipantRuntimeDispatchResult?>(null);
    }
}
