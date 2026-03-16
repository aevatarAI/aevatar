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
    : IProjectionProjector<ServiceServingSetProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServiceServingSetReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceServingSetReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceServingSetProjector(
        IProjectionWriteDispatcher<ServiceServingSetReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceServingSetReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceServingSetProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
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

        var readModel = await _documentReader.GetAsync(serviceKey, ct)
            ?? new ServiceServingSetReadModel { Id = serviceKey };
        readModel.Generation = evt.Generation;
        readModel.ActiveRolloutId = evt.RolloutId ?? string.Empty;
        readModel.ActorId = context.RootActorId;
        readModel.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(readModel.StateVersion, stateVersion);
        readModel.LastEventId = eventId;
        readModel.UpdatedAt = observedAt;
        readModel.Targets = evt.Targets
            .Select(ServiceProjectionMapping.ToServingTargetReadModel)
            .OrderByDescending(x => x.AllocationWeight)
            .ThenBy(x => x.RevisionId, StringComparer.Ordinal)
            .ToList();
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    public ValueTask CompleteAsync(
        ServiceServingSetProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
