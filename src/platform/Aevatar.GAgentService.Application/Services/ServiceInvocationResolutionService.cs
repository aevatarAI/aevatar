using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvocationResolutionService
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceTrafficViewQueryReader _trafficViewQueryReader;
    private readonly IServiceServingSetQueryReader? _servingSetQueryReader;
    private readonly IServiceRevisionArtifactStore _artifactStore;

    public ServiceInvocationResolutionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceTrafficViewQueryReader trafficViewQueryReader,
        IServiceRevisionArtifactStore artifactStore)
        : this(catalogQueryReader, trafficViewQueryReader, null, artifactStore)
    {
    }

    public ServiceInvocationResolutionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceTrafficViewQueryReader trafficViewQueryReader,
        IServiceServingSetQueryReader? servingSetQueryReader,
        IServiceRevisionArtifactStore artifactStore)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _trafficViewQueryReader = trafficViewQueryReader ?? throw new ArgumentNullException(nameof(trafficViewQueryReader));
        _servingSetQueryReader = servingSetQueryReader;
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
        var trafficView = await _trafficViewQueryReader.GetAsync(request.Identity, ct);
        var endpointView = trafficView?.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal));
        if (endpointView == null || endpointView.Targets.Count == 0)
        {
            endpointView = await BuildFallbackEndpointViewAsync(request, trafficView != null, ct);
        }
        if (endpointView == null || endpointView.Targets.Count == 0)
            throw new InvalidOperationException($"Endpoint '{request.EndpointId}' has no serving target on service '{serviceKey}'.");

        var selectedTarget = SelectTarget(endpointView.Targets, request, serviceKey);
        var artifact = await _artifactStore.GetAsync(serviceKey, selectedTarget.RevisionId, ct)
            ?? throw new InvalidOperationException(
                $"Prepared artifact for '{serviceKey}' revision '{selectedTarget.RevisionId}' was not found.");
        var endpoint = artifact.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            throw new InvalidOperationException($"Endpoint '{request.EndpointId}' was not found on service '{serviceKey}'.");

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                serviceKey,
                selectedTarget.RevisionId,
                selectedTarget.DeploymentId,
                selectedTarget.PrimaryActorId,
                selectedTarget.ServingState,
                definition.PolicyIds),
            artifact,
            endpoint);
    }

    private async Task<ServiceTrafficEndpointSnapshot?> BuildFallbackEndpointViewAsync(
        ServiceInvocationRequest request,
        bool hasTrafficView,
        CancellationToken ct)
    {
        if (_servingSetQueryReader == null)
        {
            if (hasTrafficView)
                return null;

            throw new InvalidOperationException($"Service '{ServiceKeys.Build(request.Identity!)}' has no serving traffic view.");
        }

        var servingSet = await _servingSetQueryReader.GetAsync(request.Identity!, ct);
        if (servingSet == null)
        {
            if (hasTrafficView)
                return null;

            throw new InvalidOperationException($"Service '{ServiceKeys.Build(request.Identity!)}' has no serving traffic view.");
        }

        var targets = servingSet.Targets
            .Where(x => IsEndpointEnabled(x, request.EndpointId))
            .Select(x => new ServiceTrafficTargetSnapshot(
                x.DeploymentId,
                x.RevisionId,
                x.PrimaryActorId,
                x.AllocationWeight,
                x.ServingState))
            .ToList();

        return new ServiceTrafficEndpointSnapshot(request.EndpointId, targets);
    }

    private static bool IsEndpointEnabled(ServiceServingTargetSnapshot target, string endpointId)
    {
        if (target.EnabledEndpointIds.Count == 0)
            return true;

        return target.EnabledEndpointIds.Any(x => string.Equals(x, endpointId, StringComparison.Ordinal));
    }

    private static ServiceTrafficTargetSnapshot SelectTarget(
        IReadOnlyList<ServiceTrafficTargetSnapshot> targets,
        ServiceInvocationRequest request,
        string serviceKey)
    {
        var requestedRevisionId = request.RevisionId?.Trim() ?? string.Empty;
        var selectedCandidates = targets;
        if (!string.IsNullOrWhiteSpace(requestedRevisionId))
        {
            selectedCandidates = targets
                .Where(x => string.Equals(x.RevisionId, requestedRevisionId, StringComparison.Ordinal))
                .ToList();
            if (selectedCandidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Revision '{requestedRevisionId}' has no serving target on service '{serviceKey}'.");
            }
        }

        var activeTargets = selectedCandidates
            .Where(x => x.AllocationWeight > 0 && string.Equals(x.ServingState, ServiceServingState.Active.ToString(), StringComparison.Ordinal))
            .ToList();
        if (activeTargets.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(requestedRevisionId))
            {
                throw new InvalidOperationException(
                    $"Revision '{requestedRevisionId}' is not active on service '{serviceKey}'.");
            }

            throw new InvalidOperationException("No active serving targets are available.");
        }

        var totalWeight = activeTargets.Sum(x => x.AllocationWeight);
        var seed = request.CommandId;
        if (string.IsNullOrWhiteSpace(seed))
            seed = request.CorrelationId;
        if (string.IsNullOrWhiteSpace(seed))
            seed = request.EndpointId;

        var slot = (int)(ComputeDeterministicHash(seed ?? string.Empty) % (uint)totalWeight);
        var cumulative = 0;
        foreach (var target in activeTargets)
        {
            cumulative += target.AllocationWeight;
            if (slot < cumulative)
                return target;
        }

        return activeTargets[^1];
    }

    private static uint ComputeDeterministicHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }
}
