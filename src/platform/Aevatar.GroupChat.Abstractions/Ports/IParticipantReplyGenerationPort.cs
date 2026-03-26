using Aevatar.GroupChat.Abstractions.Participants;

namespace Aevatar.GroupChat.Abstractions.Ports;

public interface IParticipantReplyGenerationPort
{
    Task<ParticipantReplyGenerationResult?> GenerateReplyAsync(
        ParticipantReplyGenerationRequest request,
        CancellationToken ct = default);
}
