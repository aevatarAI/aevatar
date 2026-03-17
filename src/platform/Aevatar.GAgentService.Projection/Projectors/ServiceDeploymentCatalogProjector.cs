using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceDeploymentCatalogProjector
    : IProjectionArtifactMaterializer<ServiceDeploymentCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceDeploymentCatalogReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceDeploymentCatalogProjector(
        IProjectionWriteDispatcher<ServiceDeploymentCatalogReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceDeploymentCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
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

        if (payload.Is(ServiceDeploymentActivatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentActivatedEvent>();
            await UpsertAsync(context.RootActorId, evt.Identity, evt.DeploymentId, eventId, stateVersion, observedAt, readModel =>
            {
                readModel.RevisionId = evt.RevisionId ?? string.Empty;
                readModel.PrimaryActorId = evt.PrimaryActorId ?? string.Empty;
                readModel.Status = evt.Status.ToString();
                readModel.ActivatedAt = evt.ActivatedAt?.ToDateTimeOffset();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.ActivatedAt, _clock.UtcNow);
            }, ct);
            return;
        }

        if (payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentDeactivatedEvent>();
            await UpsertAsync(context.RootActorId, evt.Identity, evt.DeploymentId, eventId, stateVersion, observedAt, readModel =>
            {
                readModel.RevisionId = evt.RevisionId ?? string.Empty;
                readModel.Status = ServiceDeploymentStatus.Deactivated.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.DeactivatedAt, _clock.UtcNow);
            }, ct);
            return;
        }

        if (payload.Is(ServiceDeploymentHealthChangedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceDeploymentHealthChangedEvent>();
            await UpsertAsync(context.RootActorId, evt.Identity, evt.DeploymentId, eventId, stateVersion, observedAt, readModel =>
            {
                readModel.Status = evt.Status.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
            }, ct);
        }
    }

    private async Task UpsertAsync(
        string actorId,
        ServiceIdentity? identity,
        string deploymentId,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt,
        Action<ServiceDeploymentReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceProjectionMapping.ServiceKey(identity);
        if (string.IsNullOrWhiteSpace(serviceKey) || string.IsNullOrWhiteSpace(deploymentId))
            return;

        var existing = await _documentReader.GetAsync(serviceKey, ct)
            ?? new ServiceDeploymentCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
            };
        var deployment = existing.Deployments.FirstOrDefault(x => string.Equals(x.DeploymentId, deploymentId, StringComparison.Ordinal));
        if (deployment == null)
        {
            deployment = new ServiceDeploymentReadModel
            {
                DeploymentId = deploymentId,
                UpdatedAt = _clock.UtcNow,
            };
            existing.Deployments.Add(deployment);
        }

        mutate(deployment);
        existing.ActorId = actorId;
        existing.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(existing.StateVersion, stateVersion);
        existing.LastEventId = eventId;
        existing.UpdatedAt = observedAt;
        existing.Deployments = existing.Deployments
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.DeploymentId, StringComparer.Ordinal)
            .ToList();
        await _storeDispatcher.UpsertAsync(existing, ct);
    }
}
