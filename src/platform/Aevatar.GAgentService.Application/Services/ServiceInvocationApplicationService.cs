using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.Ports;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvocationApplicationService : IServiceInvocationPort
{
    private readonly ServiceInvocationResolutionService _resolutionService;
    private readonly IServiceInvocationDispatcher _dispatcher;

    public ServiceInvocationApplicationService(
        ServiceInvocationResolutionService resolutionService,
        IServiceInvocationDispatcher dispatcher)
    {
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        var target = await _resolutionService.ResolveAsync(request, ct);
        return await _dispatcher.DispatchAsync(target, request, ct);
    }
}
