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

public sealed class ServiceConfigurationProjector
    : IProjectionProjector<ServiceConfigurationProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServiceConfigurationReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServiceConfigurationReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceConfigurationProjector(
        IProjectionWriteDispatcher<ServiceConfigurationReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceConfigurationReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceConfigurationProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceConfigurationProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(LegacyServiceConfigurationImportedEvent.Descriptor))
        {
            var evt = payload.Unpack<LegacyServiceConfigurationImportedEvent>();
            await ReplaceAsync(evt.State, ct);
            return;
        }

        if (payload.Is(ServiceBindingCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingCreatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel =>
            {
                var binding = GetOrAddBinding(readModel, evt.Spec.BindingId);
                ApplyBinding(binding, evt.Spec, retired: false);
            });
            return;
        }

        if (payload.Is(ServiceBindingUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingUpdatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel =>
            {
                var binding = GetOrAddBinding(readModel, evt.Spec.BindingId);
                ApplyBinding(binding, evt.Spec, retired: false);
            });
            return;
        }

        if (payload.Is(ServiceBindingRetiredEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingRetiredEvent>();
            await UpsertAsync(evt.Identity, ct, readModel =>
            {
                var binding = GetOrAddBinding(readModel, evt.BindingId);
                binding.Retired = true;
            });
            return;
        }

        if (payload.Is(ServiceEndpointCatalogCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceEndpointCatalogCreatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel => ApplyEndpointCatalog(readModel, evt.Spec));
            return;
        }

        if (payload.Is(ServiceEndpointCatalogUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceEndpointCatalogUpdatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel => ApplyEndpointCatalog(readModel, evt.Spec));
            return;
        }

        if (payload.Is(ServicePolicyCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyCreatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel =>
            {
                var policy = GetOrAddPolicy(readModel, evt.Spec.PolicyId);
                ApplyPolicy(policy, evt.Spec, retired: false);
            });
            return;
        }

        if (payload.Is(ServicePolicyUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyUpdatedEvent>();
            await UpsertAsync(evt.Spec.Identity, ct, readModel =>
            {
                var policy = GetOrAddPolicy(readModel, evt.Spec.PolicyId);
                ApplyPolicy(policy, evt.Spec, retired: false);
            });
            return;
        }

        if (payload.Is(ServicePolicyRetiredEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyRetiredEvent>();
            await UpsertAsync(evt.Identity, ct, readModel =>
            {
                var policy = GetOrAddPolicy(readModel, evt.PolicyId);
                policy.Retired = true;
            });
        }
    }

    public ValueTask CompleteAsync(
        ServiceConfigurationProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertAsync(ServiceIdentity identity, CancellationToken ct, Action<ServiceConfigurationReadModel> mutate)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var readModel = await _documentReader.GetAsync(serviceKey, ct)
            ?? new ServiceConfigurationReadModel
            {
                Id = serviceKey,
            };
        readModel.Identity = ToReadModel(identity);
        mutate(readModel);
        readModel.UpdatedAt = _clock.UtcNow;
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private async Task ReplaceAsync(ServiceConfigurationState? state, CancellationToken ct)
    {
        if (state?.Identity == null || string.IsNullOrWhiteSpace(state.Identity.ServiceId))
            return;

        var readModel = new ServiceConfigurationReadModel
        {
            Id = ServiceKeys.Build(state.Identity),
            Identity = ToReadModel(state.Identity),
            UpdatedAt = _clock.UtcNow,
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

        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static ServiceBindingReadModel GetOrAddBinding(ServiceConfigurationReadModel readModel, string bindingId)
    {
        var binding = readModel.Bindings.FirstOrDefault(x => string.Equals(x.BindingId, bindingId, StringComparison.Ordinal));
        if (binding != null)
            return binding;

        binding = new ServiceBindingReadModel
        {
            BindingId = bindingId ?? string.Empty,
        };
        readModel.Bindings.Add(binding);
        readModel.Bindings = readModel.Bindings.OrderBy(x => x.BindingId, StringComparer.Ordinal).ToList();
        return binding;
    }

    private static ServicePolicyReadModel GetOrAddPolicy(ServiceConfigurationReadModel readModel, string policyId)
    {
        var policy = readModel.Policies.FirstOrDefault(x => string.Equals(x.PolicyId, policyId, StringComparison.Ordinal));
        if (policy != null)
            return policy;

        policy = new ServicePolicyReadModel
        {
            PolicyId = policyId ?? string.Empty,
        };
        readModel.Policies.Add(policy);
        readModel.Policies = readModel.Policies.OrderBy(x => x.PolicyId, StringComparer.Ordinal).ToList();
        return policy;
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
