using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.Ports;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvocationResolutionService
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceRevisionArtifactStore _artifactStore;

    public ServiceInvocationResolutionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceRevisionArtifactStore artifactStore)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public async Task<ServiceInvocationResolvedTarget> ResolveAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Identity == null)
            throw new InvalidOperationException("service identity is required.");
        if (string.IsNullOrWhiteSpace(request.EndpointId))
            throw new InvalidOperationException("endpoint_id is required.");

        var service = await _catalogQueryReader.GetAsync(request.Identity, ct)
            ?? throw new InvalidOperationException($"Service '{ServiceKeys.Build(request.Identity)}' was not found.");
        var activeRevisionId = string.IsNullOrWhiteSpace(service.ActiveServingRevisionId)
            ? service.DefaultServingRevisionId
            : service.ActiveServingRevisionId;
        if (string.IsNullOrWhiteSpace(activeRevisionId))
            throw new InvalidOperationException($"Service '{service.ServiceKey}' has no active or default serving revision.");

        var artifact = await _artifactStore.GetAsync(service.ServiceKey, activeRevisionId, ct)
            ?? throw new InvalidOperationException($"Prepared artifact for '{service.ServiceKey}' revision '{activeRevisionId}' was not found.");
        var endpoint = artifact.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            throw new InvalidOperationException($"Endpoint '{request.EndpointId}' was not found on service '{service.ServiceKey}'.");

        return new ServiceInvocationResolvedTarget(service, artifact, endpoint);
    }
}
