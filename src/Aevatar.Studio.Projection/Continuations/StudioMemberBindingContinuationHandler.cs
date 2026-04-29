using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.Orchestration;

namespace Aevatar.Studio.Projection.Continuations;

/// <summary>
/// Durable business continuation for committed member binding requests.
/// </summary>
internal sealed class StudioMemberBindingContinuationHandler
    : ICommittedObservationContinuation<StudioMaterializationContext>
{
    private readonly IStudioMemberBindingContinuationDispatcher _dispatcher;

    public StudioMemberBindingContinuationHandler(
        IStudioMemberBindingContinuationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask ContinueAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null ||
            !payload.Is(StudioMemberBindingRequestedEvent.Descriptor))
        {
            return;
        }

        await _dispatcher.DispatchAsync(
            payload.Unpack<StudioMemberBindingRequestedEvent>(),
            ct);
    }
}
