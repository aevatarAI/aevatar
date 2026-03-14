using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Projectors;

public sealed class ServiceBindingProjector
    : IProjectionProjector<ServiceBindingProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ServiceBindingCatalogReadModel, string> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceBindingProjector(
        IProjectionStoreDispatcher<ServiceBindingCatalogReadModel, string> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServiceBindingProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServiceBindingProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(ServiceBindingCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingCreatedEvent>();
            await UpsertBindingAsync(evt.Spec.Identity, evt.Spec.BindingId, entry => ApplySpec(entry, evt.Spec, retired: false), ct);
            return;
        }

        if (payload.Is(ServiceBindingUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingUpdatedEvent>();
            await UpsertBindingAsync(evt.Spec.Identity, evt.Spec.BindingId, entry => ApplySpec(entry, evt.Spec, retired: false), ct);
            return;
        }

        if (payload.Is(ServiceBindingRetiredEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceBindingRetiredEvent>();
            await UpsertBindingAsync(evt.Identity, evt.BindingId, entry => entry.Retired = true, ct);
        }
    }

    public ValueTask CompleteAsync(
        ServiceBindingProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertBindingAsync(
        ServiceIdentity identity,
        string bindingId,
        Action<ServiceBindingReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var existing = await _storeDispatcher.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            existing = new ServiceBindingCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
            };
            var entry = new ServiceBindingReadModel
            {
                BindingId = bindingId ?? string.Empty,
            };
            mutate(entry);
            existing.Bindings.Add(entry);
            existing.UpdatedAt = _clock.UtcNow;
            await _storeDispatcher.UpsertAsync(existing, ct);
            return;
        }

        await _storeDispatcher.MutateAsync(serviceKey, readModel =>
        {
            var entry = readModel.Bindings.FirstOrDefault(x => string.Equals(x.BindingId, bindingId, StringComparison.Ordinal));
            if (entry == null)
            {
                entry = new ServiceBindingReadModel
                {
                    BindingId = bindingId ?? string.Empty,
                };
                readModel.Bindings.Add(entry);
            }

            mutate(entry);
            readModel.UpdatedAt = _clock.UtcNow;
        }, ct);
    }

    private static void ApplySpec(ServiceBindingReadModel entry, ServiceBindingSpec spec, bool retired)
    {
        entry.BindingId = spec.BindingId ?? string.Empty;
        entry.DisplayName = spec.DisplayName ?? string.Empty;
        entry.BindingKind = spec.BindingKind.ToString();
        entry.PolicyIds = [.. spec.PolicyIds];
        entry.Retired = retired;
        entry.TargetServiceKey = spec.ServiceRef?.Identity == null
            ? string.Empty
            : ServiceKeys.Build(spec.ServiceRef.Identity);
        entry.TargetEndpointId = spec.ServiceRef?.EndpointId ?? string.Empty;
        entry.ConnectorType = spec.ConnectorRef?.ConnectorType ?? string.Empty;
        entry.ConnectorId = spec.ConnectorRef?.ConnectorId ?? string.Empty;
        entry.SecretName = spec.SecretRef?.SecretName ?? string.Empty;
    }
}
