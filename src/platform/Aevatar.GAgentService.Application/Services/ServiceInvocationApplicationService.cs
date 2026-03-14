using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvocationApplicationService : IServiceInvocationPort
{
    private readonly ServiceInvocationResolutionService _resolutionService;
    private readonly IInvokeAdmissionAuthorizer _admissionAuthorizer;
    private readonly IServiceInvocationDispatcher _dispatcher;

    public ServiceInvocationApplicationService(
        ServiceInvocationResolutionService resolutionService,
        IInvokeAdmissionAuthorizer admissionAuthorizer,
        IServiceInvocationDispatcher dispatcher)
    {
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _admissionAuthorizer = admissionAuthorizer ?? throw new ArgumentNullException(nameof(admissionAuthorizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        var target = await _resolutionService.ResolveAsync(request, ct);
        await _admissionAuthorizer.AuthorizeAsync(
            target.Service.ServiceKey,
            target.Service.DeploymentId,
            target.Artifact,
            target.Endpoint,
            request,
            ct);
        return await _dispatcher.DispatchAsync(target, request, ct);
    }
}
