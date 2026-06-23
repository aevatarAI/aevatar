using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceCatalogProjector
    : IProjectionArtifactMaterializer<ServiceCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceCatalogReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceCatalogProjector(
        IProjectionWriteDispatcher<ServiceCatalogReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    //   Old pattern: Service artifact projectors injected document reader and incrementally mutated prior readmodel state.
    //   New principle: 服务投影器仅做 state-root overwrite; catalog definition-only, deployment/serving facts come from their readmodels.
    //   No new actor, envelope kind, projection phase, layer, or docs/canon change.
    public async ValueTask ProjectAsync(ServiceCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!ServiceCommittedStateSupport.TryGetObservedState<ServiceDefinitionState>(
                envelope,
                _clock,
                out var state,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            state?.Spec?.Identity == null)
        {
            return;
        }

        var readModel = new ServiceCatalogReadModel
        {
            DisplayName = state.Spec.DisplayName ?? string.Empty,
            DefaultServingRevisionId = state.DefaultServingRevisionId ?? string.Empty,
            Endpoints = state.Spec.Endpoints.Select(MapEndpoint).ToList(),
            PolicyIds = [.. state.Spec.PolicyIds],
            ExternalExposure = MapExternalExposure(state.Spec.ExternalExposure),
        };
        ApplyIdentity(readModel, state.Spec.Identity);
        ApplyProjectionStamp(readModel, context.RootActorId, eventId, stateVersion, observedAt);
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static void ApplyProjectionStamp(
        ServiceCatalogReadModel readModel,
        string actorId,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt)
    {
        readModel.ActorId = actorId;
        readModel.StateVersion = stateVersion;
        readModel.LastEventId = eventId;
        readModel.UpdatedAt = observedAt;
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

    private static ServiceCatalogExternalExposureReadModel? MapExternalExposure(ExternalExposure? externalExposure)
    {
        if (externalExposure == null)
        {
            return null;
        }

        return new ServiceCatalogExternalExposureReadModel
        {
            NyxidSlug = externalExposure.NyxidSlug ?? string.Empty,
            RegisteredAtUtcValue = externalExposure.RegisteredAt?.Clone(),
            Status = externalExposure.Status,
            NyxidServiceId = externalExposure.NyxidServiceId ?? string.Empty,
            DesiredSpecHash = externalExposure.DesiredSpecHash ?? string.Empty,
            RegisteredSpecHash = externalExposure.RegisteredSpecHash ?? string.Empty,
            LastError = externalExposure.LastError ?? string.Empty,
            Attempt = externalExposure.Attempt,
            NextAttemptAtUtcValue = externalExposure.NextAttemptAt?.Clone(),
            CredentialKid = externalExposure.CredentialKid ?? string.Empty,
            ExposureDesired = externalExposure.ExposureDesired,
        };
    }
}
