using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Participants;

internal sealed class ParticipantRuntimeDispatchRouter : IParticipantRuntimeDispatchPort
{
    private readonly IReadOnlyList<IParticipantRuntimeDispatcher> _dispatchers;

    public ParticipantRuntimeDispatchRouter(IEnumerable<IParticipantRuntimeDispatcher> dispatchers)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);
        _dispatchers = dispatchers.ToList();
    }

    public Task<ParticipantRuntimeDispatchResult?> DispatchAsync(
        ParticipantRuntimeDispatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dispatcher = _dispatchers.FirstOrDefault(x => x.CanDispatch(request.Binding));
        return dispatcher == null
            ? Task.FromResult<ParticipantRuntimeDispatchResult?>(null)
            : dispatcher.DispatchAsync(request, ct);
    }
}
