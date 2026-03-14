using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRevisionCatalogProjector
    : IProjectionProjector<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ServiceRevisionCatalogReadModel, string> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceRevisionCatalogProjector(
        IProjectionStoreDispatcher<ServiceRevisionCatalogReadModel, string> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceRevisionCatalogProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceRevisionCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(ServiceRevisionCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionCreatedEvent>();
            await UpsertRevisionAsync(evt.Spec.Identity, evt.Spec.RevisionId, entry =>
            {
                entry.RevisionId = evt.Spec.RevisionId ?? string.Empty;
                entry.ImplementationKind = evt.Spec.ImplementationKind.ToString();
                entry.Status = ServiceRevisionStatus.Created.ToString();
                entry.CreatedAt = ToDateTimeOffset(evt.CreatedAt);
            }, ct);
            return;
        }

        if (payload.Is(ServiceRevisionPreparedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionPreparedEvent>();
            await UpsertRevisionAsync(evt.Identity, evt.RevisionId, entry =>
            {
                entry.RevisionId = evt.RevisionId ?? string.Empty;
                entry.ImplementationKind = evt.ImplementationKind.ToString();
                entry.Status = ServiceRevisionStatus.Prepared.ToString();
                entry.ArtifactHash = evt.ArtifactHash ?? string.Empty;
                entry.FailureReason = string.Empty;
                entry.PreparedAt = ToDateTimeOffset(evt.PreparedAt);
                entry.Endpoints = evt.Endpoints.Select(MapEndpoint).ToList();
            }, ct);
            return;
        }

        if (payload.Is(ServiceRevisionPreparationFailedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionPreparationFailedEvent>();
            await UpsertRevisionAsync(evt.Identity, evt.RevisionId, entry =>
            {
                entry.RevisionId = evt.RevisionId ?? string.Empty;
                entry.Status = ServiceRevisionStatus.PreparationFailed.ToString();
                entry.FailureReason = evt.FailureReason ?? string.Empty;
            }, ct);
            return;
        }

        if (payload.Is(ServiceRevisionPublishedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionPublishedEvent>();
            await UpsertRevisionAsync(evt.Identity, evt.RevisionId, entry =>
            {
                entry.RevisionId = evt.RevisionId ?? string.Empty;
                entry.Status = ServiceRevisionStatus.Published.ToString();
                entry.PublishedAt = ToDateTimeOffset(evt.PublishedAt);
            }, ct);
            return;
        }

        if (payload.Is(ServiceRevisionRetiredEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionRetiredEvent>();
            await UpsertRevisionAsync(evt.Identity, evt.RevisionId, entry =>
            {
                entry.RevisionId = evt.RevisionId ?? string.Empty;
                entry.Status = ServiceRevisionStatus.Retired.ToString();
                entry.RetiredAt = ToDateTimeOffset(evt.RetiredAt);
            }, ct);
        }
    }

    public ValueTask CompleteAsync(
        ServiceRevisionCatalogProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertRevisionAsync(
        ServiceIdentity identity,
        string revisionId,
        Action<ServiceRevisionEntryReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var existing = await _storeDispatcher.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            existing = new ServiceRevisionCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
            };
            var entry = new ServiceRevisionEntryReadModel();
            mutate(entry);
            existing.Revisions.Add(entry);
            existing.UpdatedAt = _clock.UtcNow;
            await _storeDispatcher.UpsertAsync(existing, ct);
            return;
        }

        await _storeDispatcher.MutateAsync(serviceKey, readModel =>
        {
            var entry = readModel.Revisions.FirstOrDefault(x =>
                string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
            if (entry == null)
            {
                entry = new ServiceRevisionEntryReadModel
                {
                    RevisionId = revisionId ?? string.Empty,
                };
                readModel.Revisions.Add(entry);
            }

            mutate(entry);
            readModel.UpdatedAt = _clock.UtcNow;
        }, ct);
    }

    private static ServiceCatalogEndpointReadModel MapEndpoint(ServiceEndpointDescriptor endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind.ToString(),
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
        };

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        return timestamp == null ? null : timestamp.ToDateTimeOffset();
    }
}
