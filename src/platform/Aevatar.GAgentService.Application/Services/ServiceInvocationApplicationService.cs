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
        ArgumentNullException.ThrowIfNull(request);
        var normalizedRequest = NormalizeRequest(request);
        var target = await _resolutionService.ResolveAsync(normalizedRequest, ct);
        await _admissionAuthorizer.AuthorizeAsync(
            target.Service.ServiceKey,
            target.Service.DeploymentId,
            target.Artifact,
            target.Endpoint,
            normalizedRequest,
            ct);
        return await _dispatcher.DispatchAsync(target, normalizedRequest, ct);
    }

    private static ServiceInvocationRequest NormalizeRequest(ServiceInvocationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CommandId) || !string.IsNullOrWhiteSpace(request.CorrelationId))
            return request;

        var normalized = request.Clone();
        normalized.CommandId = Guid.NewGuid().ToString("N");
        normalized.CorrelationId = normalized.CommandId;
        return normalized;
    }
}
