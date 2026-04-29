using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;

namespace Aevatar.Studio.Projection.Continuations;

internal sealed class StudioMemberBindingObservationHandler
{
    private readonly IStudioMemberBindingContinuationDispatcher _dispatcher;

    public StudioMemberBindingObservationHandler(
        IStudioMemberBindingContinuationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask HandleAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload is null ||
            !payload.Is(StudioMemberBindingRequestedEvent.Descriptor))
        {
            return;
        }

        await _dispatcher.DispatchAsync(
            payload.Unpack<StudioMemberBindingRequestedEvent>(),
            ct);
    }
}
