using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRevisionCatalogProjector
    : IProjectionMaterializer<ServiceRevisionCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceRevisionCatalogReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceRevisionCatalogProjector(
        IProjectionWriteDispatcher<ServiceRevisionCatalogReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceRevisionCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedPayload(
                envelope,
                _clock,
                out var payload,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            payload == null)
        {
            return;
        }

        if (payload.Is(ServiceRevisionCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRevisionCreatedEvent>();
            await UpsertRevisionAsync(context.RootActorId, evt.Spec.Identity, evt.Spec.RevisionId, eventId, stateVersion, observedAt, entry =>
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
            await UpsertRevisionAsync(context.RootActorId, evt.Identity, evt.RevisionId, eventId, stateVersion, observedAt, entry =>
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
            await UpsertRevisionAsync(context.RootActorId, evt.Identity, evt.RevisionId, eventId, stateVersion, observedAt, entry =>
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
            await UpsertRevisionAsync(context.RootActorId, evt.Identity, evt.RevisionId, eventId, stateVersion, observedAt, entry =>
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
            await UpsertRevisionAsync(context.RootActorId, evt.Identity, evt.RevisionId, eventId, stateVersion, observedAt, entry =>
            {
                entry.RevisionId = evt.RevisionId ?? string.Empty;
                entry.Status = ServiceRevisionStatus.Retired.ToString();
                entry.RetiredAt = ToDateTimeOffset(evt.RetiredAt);
            }, ct);
        }
    }

    private async Task UpsertRevisionAsync(
        string actorId,
        ServiceIdentity identity,
        string revisionId,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt,
        Action<ServiceRevisionEntryReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var existing = await _documentReader.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            existing = new ServiceRevisionCatalogReadModel
            {
                Id = serviceKey,
            };
            var entry = new ServiceRevisionEntryReadModel();
            mutate(entry);
            existing.Revisions.Add(entry);
            existing.ActorId = actorId;
            existing.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(existing.StateVersion, stateVersion);
            existing.LastEventId = eventId;
            existing.UpdatedAt = observedAt;
            await _storeDispatcher.UpsertAsync(existing, ct);
            return;
        }

        var existingEntry = existing.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (existingEntry == null)
        {
            existingEntry = new ServiceRevisionEntryReadModel
            {
                RevisionId = revisionId ?? string.Empty,
            };
            existing.Revisions.Add(existingEntry);
        }

        mutate(existingEntry);
        existing.ActorId = actorId;
        existing.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(existing.StateVersion, stateVersion);
        existing.LastEventId = eventId;
        existing.UpdatedAt = observedAt;
        await _storeDispatcher.UpsertAsync(existing, ct);
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
