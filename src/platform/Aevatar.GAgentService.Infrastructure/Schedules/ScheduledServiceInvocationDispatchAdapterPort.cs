using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchAdapterPort : IActorDispatchPort
{
    private readonly IActorDispatchPort _inner;
    private readonly Func<IServiceInvocationPort?> _serviceInvocationPortResolver;

    public ScheduledServiceInvocationDispatchAdapterPort(
        IActorDispatchPort inner,
        Func<IServiceInvocationPort?> serviceInvocationPortResolver)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _serviceInvocationPortResolver = serviceInvocationPortResolver ?? throw new ArgumentNullException(nameof(serviceInvocationPortResolver));
    }

    public async Task<DispatchAdmission> DispatchAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(
                actorId.Trim(),
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                StringComparison.Ordinal) ||
            envelope.Payload?.TryUnpack<ServiceInvocationRequest>(out var serviceInvocationRequest) != true)
        {
            return await _inner.DispatchAsync(actorId, envelope, ct);
        }

        var serviceInvocationPort = _serviceInvocationPortResolver()
            ?? throw new InvalidOperationException("Service invocation scheduled dispatch adapter is not registered.");
        var receipt = await serviceInvocationPort.InvokeAsync(serviceInvocationRequest, ct);
        return new DispatchAdmission(
            true,
            receipt.CommandId,
            DateTimeOffset.UtcNow,
            receipt.TargetActorId,
            receipt.CorrelationId);
    }
}
