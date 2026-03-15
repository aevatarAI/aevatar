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
    : IProjectionProjector<ServiceTrafficViewProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServiceTrafficViewReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceTrafficViewReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceTrafficViewProjector(
        IProjectionWriteDispatcher<ServiceTrafficViewReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceTrafficViewReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceTrafficViewProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceTrafficViewProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null || !normalized.Payload.Is(ServiceServingSetUpdatedEvent.Descriptor))
            return;

        var evt = normalized.Payload.Unpack<ServiceServingSetUpdatedEvent>();
        var serviceKey = ServiceProjectionMapping.ServiceKey(evt.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = await _documentReader.GetAsync(serviceKey, ct)
            ?? new ServiceTrafficViewReadModel { Id = serviceKey };
        readModel.Generation = evt.Generation;
        readModel.ActiveRolloutId = evt.RolloutId ?? string.Empty;
        readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.UpdatedAt, _clock.UtcNow);
        readModel.Endpoints = evt.Targets
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
            .ToList();
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    public ValueTask CompleteAsync(
        ServiceTrafficViewProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
