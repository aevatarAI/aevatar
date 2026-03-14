using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceCatalogProjector
    : IProjectionProjector<ServiceCatalogProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServiceCatalogReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceCatalogProjector(
        IProjectionWriteDispatcher<ServiceCatalogReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceCatalogReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceCatalogProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        ArgumentNullException.ThrowIfNull(envelope);
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(ServiceDefinitionCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDefinitionCreatedEvent>();
            await UpsertDefinitionAsync(evt.Spec.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Spec.Identity);
                readModel.DisplayName = evt.Spec.DisplayName ?? string.Empty;
                readModel.Endpoints = evt.Spec.Endpoints.Select(MapEndpoint).ToList();
                readModel.PolicyIds = [.. evt.Spec.PolicyIds];
            }, ct);
            return;
        }

        if (payload.Is(ServiceDefinitionUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDefinitionUpdatedEvent>();
            await UpsertDefinitionAsync(evt.Spec.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Spec.Identity);
                readModel.DisplayName = evt.Spec.DisplayName ?? string.Empty;
                readModel.Endpoints = evt.Spec.Endpoints.Select(MapEndpoint).ToList();
                readModel.PolicyIds = [.. evt.Spec.PolicyIds];
            }, ct);
            return;
        }

        if (payload.Is(DefaultServingRevisionChangedEvent.Descriptor))
        {
            var evt = payload.Unpack<DefaultServingRevisionChangedEvent>();
            await UpsertDefinitionAsync(evt.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Identity);
                readModel.DefaultServingRevisionId = evt.RevisionId ?? string.Empty;
            }, ct);
            return;
        }

        if (payload.Is(ServiceDeploymentActivatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentActivatedEvent>();
            await UpsertDefinitionAsync(evt.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Identity);
                readModel.ActiveServingRevisionId = evt.RevisionId ?? string.Empty;
                readModel.DeploymentId = evt.DeploymentId ?? string.Empty;
                readModel.PrimaryActorId = evt.PrimaryActorId ?? string.Empty;
                readModel.DeploymentStatus = evt.Status.ToString();
            }, ct);
            return;
        }

        if (payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentDeactivatedEvent>();
            await UpsertDefinitionAsync(evt.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Identity);
                readModel.DeploymentStatus = ServiceDeploymentStatus.Deactivated.ToString();
            }, ct);
            return;
        }

        if (payload.Is(ServiceDeploymentHealthChangedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentHealthChangedEvent>();
            await UpsertDefinitionAsync(evt.Identity, readModel =>
            {
                ApplyIdentity(readModel, evt.Identity);
                readModel.DeploymentStatus = evt.Status.ToString();
            }, ct);
        }
    }

    public ValueTask CompleteAsync(
        ServiceCatalogProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertDefinitionAsync(
        ServiceIdentity identity,
        Action<ServiceCatalogReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var existing = await _documentReader.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            existing = new ServiceCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
            };
            ApplyIdentity(existing, identity);
            mutate(existing);
            existing.UpdatedAt = _clock.UtcNow;
            await _storeDispatcher.UpsertAsync(existing, ct);
            return;
        }

        mutate(existing);
        existing.UpdatedAt = _clock.UtcNow;
        await _storeDispatcher.UpsertAsync(existing, ct);
    }

    private static void ApplyIdentity(ServiceCatalogReadModel readModel, ServiceIdentity identity)
    {
        readModel.Id = ServiceKeys.Build(identity);
        readModel.TenantId = identity.TenantId ?? string.Empty;
        readModel.AppId = identity.AppId ?? string.Empty;
        readModel.Namespace = identity.Namespace ?? string.Empty;
        readModel.ServiceId = identity.ServiceId ?? string.Empty;
    }

    private static ServiceCatalogEndpointReadModel MapEndpoint(ServiceEndpointSpec endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind.ToString(),
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
        };
}
