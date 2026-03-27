using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Application.Participants;

internal interface IParticipantRuntimeDispatcher
{
    bool CanDispatch(GroupParticipantRuntimeBindingSnapshot binding);

    Task<ParticipantRuntimeDispatchResult?> DispatchAsync(
        ParticipantRuntimeDispatchRequest request,
        CancellationToken ct = default);
}
