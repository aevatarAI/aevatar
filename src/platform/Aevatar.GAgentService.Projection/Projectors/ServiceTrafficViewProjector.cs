using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceTrafficViewProjector
    : ICurrentStateProjectionMaterializer<ServiceTrafficViewProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceTrafficViewReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceTrafficViewProjector(
        IProjectionWriteDispatcher<ServiceTrafficViewReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceTrafficViewProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
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

        var readModel = new ServiceTrafficViewReadModel
        {
            Id = serviceKey,
            Generation = evt.Generation,
            ActiveRolloutId = evt.RolloutId ?? string.Empty,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = observedAt,
            Endpoints = evt.Targets
                .SelectMany(target => target.EnabledEndpointIds.Select(endpointId => new
                {
                    EndpointId = endpointId ?? string.Empty,
                    Target = ServiceProjectionMapping.ToTrafficTargetReadModel(target),
                }))
                .Where(x => !string.IsNullOrWhiteSpace(x.EndpointId))
                .GroupBy(x => x.EndpointId, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(group => new ServiceTrafficEndpointReadModel
                {
                    EndpointId = group.Key,
                    Targets = group.Select(x => x.Target)
                        .OrderByDescending(x => x.AllocationWeight)
                        .ThenBy(x => x.RevisionId, StringComparer.Ordinal)
                        .ToList(),
                })
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

}
