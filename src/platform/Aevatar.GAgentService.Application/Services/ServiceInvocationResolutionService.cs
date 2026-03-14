using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

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

        var serviceKey = ServiceKeys.Build(request.Identity);
        var definition = await _catalogQueryReader.GetAsync(request.Identity, ct)
            ?? throw new InvalidOperationException($"Service '{serviceKey}' was not found.");
        if (string.IsNullOrWhiteSpace(definition.ActiveServingRevisionId) ||
            string.IsNullOrWhiteSpace(definition.PrimaryActorId) ||
            !string.Equals(definition.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Service '{serviceKey}' has no active deployment.");
        }

        var artifact = await _artifactStore.GetAsync(serviceKey, definition.ActiveServingRevisionId, ct)
            ?? throw new InvalidOperationException(
                $"Prepared artifact for '{serviceKey}' revision '{definition.ActiveServingRevisionId}' was not found.");
        var endpoint = artifact.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            throw new InvalidOperationException($"Endpoint '{request.EndpointId}' was not found on service '{serviceKey}'.");

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                serviceKey,
                definition.ActiveServingRevisionId,
                definition.DeploymentId,
                definition.PrimaryActorId,
                definition.DeploymentStatus,
                definition.PolicyIds),
            artifact,
            endpoint);
    }
}
