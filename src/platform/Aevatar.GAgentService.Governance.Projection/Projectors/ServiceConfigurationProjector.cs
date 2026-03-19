using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Projectors;

public sealed class ServiceConfigurationProjector
    : IProjectionArtifactMaterializer<ServiceConfigurationProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceConfigurationReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceConfigurationProjector(
        IProjectionWriteDispatcher<ServiceConfigurationReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceConfigurationProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceConfigurationState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        if (state?.Identity == null || string.IsNullOrWhiteSpace(state.Identity.ServiceId))
            return;

        var readModel = new ServiceConfigurationReadModel
        {
            Id = ServiceKeys.Build(state.Identity),
            Identity = ToReadModel(state.Identity),
            Bindings = state.Bindings
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x =>
                {
                    var readModel = new ServiceBindingReadModel();
                    ApplyBinding(readModel, x.Value.Spec ?? new ServiceBindingSpec(), x.Value.Retired);
                    return readModel;
                })
                .ToList(),
            Endpoints = state.EndpointCatalog?.Endpoints == null
                ? []
                : state.EndpointCatalog.Endpoints
                    .Select(x => new ServiceEndpointExposureReadModel
                    {
                        EndpointId = x.EndpointId ?? string.Empty,
                        DisplayName = x.DisplayName ?? string.Empty,
                        Kind = x.Kind,
                        RequestTypeUrl = x.RequestTypeUrl ?? string.Empty,
                        ResponseTypeUrl = x.ResponseTypeUrl ?? string.Empty,
                        Description = x.Description ?? string.Empty,
                        ExposureKind = x.ExposureKind,
                        PolicyIds = [.. x.PolicyIds],
                    })
                    .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
                    .ToList(),
            Policies = state.Policies
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x =>
                {
                    var readModel = new ServicePolicyReadModel();
                    ApplyPolicy(readModel, x.Value.Spec ?? new ServicePolicySpec(), x.Value.Retired);
                    return readModel;
                })
                .ToList(),
        };

        ApplyProjectionStamp(
            readModel,
            context.RootActorId,
            stateEvent.EventId ?? string.Empty,
            stateEvent.Version,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow));
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static void ApplyProjectionStamp(
        ServiceConfigurationReadModel readModel,
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

    private static void ApplyBinding(ServiceBindingReadModel target, ServiceBindingSpec spec, bool retired)
    {
        target.BindingId = spec.BindingId ?? string.Empty;
        target.DisplayName = spec.DisplayName ?? string.Empty;
        target.BindingKind = spec.BindingKind;
        target.PolicyIds = [.. spec.PolicyIds];
        target.Retired = retired;
        target.ServiceRef = spec.ServiceRef == null ? null : new BoundServiceReferenceReadModel
        {
            Identity = ToReadModel(spec.ServiceRef.Identity),
            EndpointId = spec.ServiceRef.EndpointId ?? string.Empty,
        };
        target.ConnectorRef = spec.ConnectorRef == null ? null : new BoundConnectorReferenceReadModel
        {
            ConnectorType = spec.ConnectorRef.ConnectorType ?? string.Empty,
            ConnectorId = spec.ConnectorRef.ConnectorId ?? string.Empty,
        };
        target.SecretRef = spec.SecretRef == null ? null : new BoundSecretReferenceReadModel
        {
            SecretName = spec.SecretRef.SecretName ?? string.Empty,
        };
    }

    private static void ApplyEndpointCatalog(ServiceConfigurationReadModel readModel, ServiceEndpointCatalogSpec spec)
    {
        readModel.Endpoints = (spec.Endpoints ?? [])
            .Select(x => new ServiceEndpointExposureReadModel
            {
                EndpointId = x.EndpointId ?? string.Empty,
                DisplayName = x.DisplayName ?? string.Empty,
                Kind = x.Kind,
                RequestTypeUrl = x.RequestTypeUrl ?? string.Empty,
                ResponseTypeUrl = x.ResponseTypeUrl ?? string.Empty,
                Description = x.Description ?? string.Empty,
                ExposureKind = x.ExposureKind,
                PolicyIds = [.. x.PolicyIds],
            })
            .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
            .ToList();
    }

    private static void ApplyPolicy(ServicePolicyReadModel target, ServicePolicySpec spec, bool retired)
    {
        target.PolicyId = spec.PolicyId ?? string.Empty;
        target.DisplayName = spec.DisplayName ?? string.Empty;
        target.ActivationRequiredBindingIds = [.. spec.ActivationRequiredBindingIds];
        target.InvokeAllowedCallerServiceKeys = [.. spec.InvokeAllowedCallerServiceKeys];
        target.InvokeRequiresActiveDeployment = spec.InvokeRequiresActiveDeployment;
        target.Retired = retired;
    }

    private static ServiceIdentityReadModel ToReadModel(ServiceIdentity? identity)
    {
        return new ServiceIdentityReadModel
        {
            TenantId = identity?.TenantId ?? string.Empty,
            AppId = identity?.AppId ?? string.Empty,
            Namespace = identity?.Namespace ?? string.Empty,
            ServiceId = identity?.ServiceId ?? string.Empty,
        };
    }
}
