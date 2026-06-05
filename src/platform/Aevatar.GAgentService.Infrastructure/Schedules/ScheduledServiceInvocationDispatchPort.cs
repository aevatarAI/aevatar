using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
{
    private readonly IServiceInvocationPort _serviceInvocationPort;

    public ScheduledServiceInvocationDispatchPort(IServiceInvocationPort serviceInvocationPort)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
    }

    public async Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _serviceInvocationPort.InvokeAsync(request, ct);
        return new ScheduledServiceInvocationDispatchReceipt(
            true,
            receipt.CommandId ?? string.Empty,
            receipt.TargetActorId ?? string.Empty,
            receipt.CorrelationId ?? string.Empty);
    }
}
