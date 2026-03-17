using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceServingSetProjector
    : ICurrentStateProjectionMaterializer<ServiceServingSetProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceServingSetReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceServingSetProjector(
        IProjectionWriteDispatcher<ServiceServingSetReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceServingSetProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedPayload(
                envelope,
                _clock,
                out var payload,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            payload == null ||
            !payload.Is(ServiceServingSetUpdatedEvent.Descriptor))
        {
            return;
        }

        var evt = payload.Unpack<ServiceServingSetUpdatedEvent>();
        var serviceKey = ServiceProjectionMapping.ServiceKey(evt.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = new ServiceServingSetReadModel
        {
            Id = serviceKey,
            Generation = evt.Generation,
            ActiveRolloutId = evt.RolloutId ?? string.Empty,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = observedAt,
            Targets = evt.Targets
                .Select(ServiceProjectionMapping.ToServingTargetReadModel)
                .OrderByDescending(x => x.AllocationWeight)
                .ThenBy(x => x.RevisionId, StringComparer.Ordinal)
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

}
