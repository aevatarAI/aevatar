using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRolloutCommandObservationProjector
    : IProjectionArtifactMaterializer<ServiceRolloutProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceRolloutCommandObservationReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceRolloutCommandObservationProjector(
        IProjectionWriteDispatcher<ServiceRolloutCommandObservationReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceRolloutProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedPayload(
                envelope,
                _clock,
                out var payload,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            payload == null ||
            !payload.Is(ServiceRolloutCommandObservedEvent.Descriptor))
        {
            return;
        }

        var evt = payload.Unpack<ServiceRolloutCommandObservedEvent>();
        if (string.IsNullOrWhiteSpace(evt.CommandId))
            return;

        var serviceKey = ServiceProjectionMapping.ServiceKey(evt.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        await _storeDispatcher.UpsertAsync(
            new ServiceRolloutCommandObservationReadModel
            {
                Id = evt.CommandId,
                ActorId = context.RootActorId,
                StateVersion = stateVersion,
                LastEventId = eventId,
                ServiceKey = serviceKey,
                RolloutId = evt.RolloutId ?? string.Empty,
                CommandId = evt.CommandId,
                CorrelationId = evt.CorrelationId ?? string.Empty,
                Status = (int)evt.Status,
                WasNoOp = evt.WasNoOp,
                ObservedAt = ServiceProjectionMapping.FromTimestamp(evt.ObservedAt, observedAt),
            },
            ct);
    }
}
