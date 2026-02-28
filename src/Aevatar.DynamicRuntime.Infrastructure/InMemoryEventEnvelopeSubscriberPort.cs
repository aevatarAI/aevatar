using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopeSubscriberPort : IEventEnvelopeSubscriberPort
{
    private readonly InMemoryEventEnvelopeBusState _state;

    public InMemoryEventEnvelopeSubscriberPort(InMemoryEventEnvelopeBusState state)
    {
        _state = state;
    }

    public Task<EnvelopeLeaseResult> SubscribeAsync(EnvelopeSubscribeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.LeaseId))
            return Task.FromResult(new EnvelopeLeaseResult(false, string.Empty, "ENVELOPE_LEASE_INVALID", "lease_id is required"));
        if (string.IsNullOrWhiteSpace(request.StackId) || string.IsNullOrWhiteSpace(request.ServiceName))
            return Task.FromResult(new EnvelopeLeaseResult(false, request.LeaseId, "ENVELOPE_LEASE_INVALID", "stack_id/service_name is required"));

        _state.Leases[request.LeaseId] = request;
        return Task.FromResult(new EnvelopeLeaseResult(true, request.LeaseId));
    }
}
