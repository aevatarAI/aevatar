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
    private readonly IProjectionClock _clock;

    public ServiceDeploymentCatalogProjector(
        IProjectionWriteDispatcher<ServiceDeploymentCatalogReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    //   Old pattern: Service artifact projectors injected document reader and incrementally mutated prior readmodel state.
    //   New principle: 服务投影器仅做 state-root overwrite; catalog definition-only, deployment/serving facts come from their readmodels.
    //   No new actor, envelope kind, projection phase, layer, or docs/canon change.
    public async ValueTask ProjectAsync(ServiceDeploymentCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedState<ServiceDeploymentState>(
                envelope,
                _clock,
                out var state,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            state?.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceProjectionMapping.ServiceKey(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = new ServiceDeploymentCatalogReadModel
        {
            Id = serviceKey,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = observedAt,
            Deployments = state.Deployments
                .Values
                .Select(MapDeployment)
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.DeploymentId, StringComparer.Ordinal)
                .ToList(),
            ActivationFailures = state.ActivationFailures
                .Values
                .Select(MapActivationFailure)
                .OrderByDescending(x => x.OccurredAtUtcValue?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
                .ThenBy(x => x.RevisionId, StringComparer.Ordinal)
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static ServiceDeploymentReadModel MapDeployment(ServiceDeploymentRecord source) =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            Status = source.Status.ToString(),
            ActivatedAt = source.ActivatedAt?.ToDateTimeOffset(),
            UpdatedAt = ServiceProjectionMapping.FromTimestamp(source.UpdatedAt, DateTimeOffset.UnixEpoch),
            ArtifactHash = source.ArtifactHash ?? string.Empty,
        };

    private static ServiceDeploymentActivationFailureReadModel MapActivationFailure(
        ServiceDeploymentActivationFailureRecord source) =>
        new()
        {
            RevisionId = source.RevisionId ?? string.Empty,
            FailureCode = source.FailureCode,
            FailureReason = source.FailureReason ?? string.Empty,
            OccurredAtUtcValue = source.OccurredAt?.Clone(),
            ActivationAttemptId = source.ActivationAttemptId ?? string.Empty,
        };
}
