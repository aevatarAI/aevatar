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

public sealed class ServicePolicyProjector
    : IProjectionProjector<ServicePolicyProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ServicePolicyCatalogReadModel> _storeDispatcher;
    private readonly IProjectionDocumentReader<ServicePolicyCatalogReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServicePolicyProjector(
        IProjectionWriteDispatcher<ServicePolicyCatalogReadModel> storeDispatcher,
        IProjectionDocumentReader<ServicePolicyCatalogReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ServicePolicyProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(ServicePolicyProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = context;
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var payload = normalized.Payload;
        if (payload.Is(ServicePolicyCreatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyCreatedEvent>();
            await UpsertPolicyAsync(evt.Spec.Identity, evt.Spec.PolicyId, entry => ApplySpec(entry, evt.Spec, retired: false), ct);
            return;
        }

        if (payload.Is(ServicePolicyUpdatedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyUpdatedEvent>();
            await UpsertPolicyAsync(evt.Spec.Identity, evt.Spec.PolicyId, entry => ApplySpec(entry, evt.Spec, retired: false), ct);
            return;
        }

        if (payload.Is(ServicePolicyRetiredEvent.Descriptor))
        {
            var evt = payload.Unpack<ServicePolicyRetiredEvent>();
            await UpsertPolicyAsync(evt.Identity, evt.PolicyId, entry => entry.Retired = true, ct);
        }
    }

    public ValueTask CompleteAsync(
        ServicePolicyProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task UpsertPolicyAsync(
        ServiceIdentity identity,
        string policyId,
        Action<ServicePolicyReadModel> mutate,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var existing = await _documentReader.GetAsync(serviceKey, ct);
        if (existing == null)
        {
            existing = new ServicePolicyCatalogReadModel
            {
                Id = serviceKey,
                UpdatedAt = _clock.UtcNow,
            };
            var entry = new ServicePolicyReadModel
            {
                PolicyId = policyId ?? string.Empty,
            };
            mutate(entry);
            existing.Policies.Add(entry);
            existing.UpdatedAt = _clock.UtcNow;
            await _storeDispatcher.UpsertAsync(existing, ct);
            return;
        }

        var existingEntry = existing.Policies.FirstOrDefault(x => string.Equals(x.PolicyId, policyId, StringComparison.Ordinal));
        if (existingEntry == null)
        {
            existingEntry = new ServicePolicyReadModel
            {
                PolicyId = policyId ?? string.Empty,
            };
            existing.Policies.Add(existingEntry);
        }

        mutate(existingEntry);
        existing.UpdatedAt = _clock.UtcNow;
        await _storeDispatcher.UpsertAsync(existing, ct);
    }

    private static void ApplySpec(ServicePolicyReadModel entry, ServicePolicySpec spec, bool retired)
    {
        entry.PolicyId = spec.PolicyId ?? string.Empty;
        entry.DisplayName = spec.DisplayName ?? string.Empty;
        entry.ActivationRequiredBindingIds = [.. spec.ActivationRequiredBindingIds];
        entry.InvokeAllowedCallerServiceKeys = [.. spec.InvokeAllowedCallerServiceKeys];
        entry.InvokeRequiresActiveDeployment = spec.InvokeRequiresActiveDeployment;
        entry.Retired = retired;
    }
}
