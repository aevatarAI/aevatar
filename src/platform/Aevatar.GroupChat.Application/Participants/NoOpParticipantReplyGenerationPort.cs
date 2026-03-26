using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Participants;

public sealed class NoOpParticipantReplyGenerationPort : IParticipantReplyGenerationPort
{
    public Task<ParticipantReplyGenerationResult?> GenerateReplyAsync(
        ParticipantReplyGenerationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<ParticipantReplyGenerationResult?>(null);
    }
}
