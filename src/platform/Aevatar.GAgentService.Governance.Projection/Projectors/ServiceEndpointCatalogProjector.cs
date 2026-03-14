using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Projectors;

public sealed class ServiceEndpointCatalogProjector
    : IProjectionProjector<ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServiceEndpointCatalogReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceEndpointCatalogReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceEndpointCatalogProjector(
        IProjectionWriteDispatcher<ServiceEndpointCatalogReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceEndpointCatalogReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceEndpointCatalogProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceEndpointCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(ServiceEndpointCatalogCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceEndpointCatalogCreatedEvent>();
            await UpsertCatalogAsync(evt.Spec.Identity, evt.Spec.Endpoints, ct);
            return;
        }

        if (payload.Is(ServiceEndpointCatalogUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceEndpointCatalogUpdatedEvent>();
            await UpsertCatalogAsync(evt.Spec.Identity, evt.Spec.Endpoints, ct);
        }
    }

    public ValueTask CompleteAsync(
        ServiceEndpointCatalogProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertCatalogAsync(
        ServiceIdentity identity,
        IEnumerable<ServiceEndpointExposureSpec> endpoints,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var items = endpoints.Select(MapEndpoint).ToList();
        var existing = await _documentReader.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            await _storeDispatcher.UpsertAsync(new ServiceEndpointCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
                Endpoints = items,
            }, ct);
            return;
        }

        existing.Endpoints = items.Select(x => x.DeepClone()).ToList();
        existing.UpdatedAt = _clock.UtcNow;
        await _storeDispatcher.UpsertAsync(existing, ct);
    }

    private static ServiceEndpointExposureReadModel MapEndpoint(ServiceEndpointExposureSpec endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind.ToString(),
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
            ExposureKind = endpoint.ExposureKind.ToString(),
            PolicyIds = [.. endpoint.PolicyIds],
        };
}
