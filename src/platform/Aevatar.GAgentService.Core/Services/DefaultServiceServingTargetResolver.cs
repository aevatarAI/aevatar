using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;

namespace Aevatar.GAgentService.Core.Services;

public sealed class DefaultServiceServingTargetResolver : IServiceServingTargetResolver
{
    private readonly IServiceDeploymentCatalogQueryReader _deploymentQueryReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;

    public DefaultServiceServingTargetResolver(
        IServiceDeploymentCatalogQueryReader deploymentQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader)
    {
        _deploymentQueryReader = deploymentQueryReader ?? throw new ArgumentNullException(nameof(deploymentQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
    }

    public async Task<IReadOnlyList<ServiceServingTargetSpec>> ResolveTargetsAsync(
        ServiceIdentity identity,
        IEnumerable<ServiceServingTargetSpec> targets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(targets);

        var requestedTargets = targets.ToList();
        foreach (var target in requestedTargets)
        {
            if (string.IsNullOrWhiteSpace(target.RevisionId))
                throw new InvalidOperationException("revision_id is required for serving targets.");
        }

        var serviceKey = ServiceKeys.Build(identity);
        var deployments = await _deploymentQueryReader.GetAsync(identity, ct)
            ?? throw new InvalidOperationException($"Deployments for '{serviceKey}' were not found.");
        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(identity, ct);
        var deploymentByRevision = deployments.Deployments
            .Where(x => string.Equals(x.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
            .GroupBy(x => x.RevisionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var resolved = new List<ServiceServingTargetSpec>();
        foreach (var target in requestedTargets)
        {
            resolved.Add(ResolveTarget(identity, target, deploymentByRevision, revisionCatalog));
        }

        return resolved;
    }

    private static ServiceServingTargetSpec ResolveTarget(
        ServiceIdentity identity,
        ServiceServingTargetSpec target,
        IReadOnlyDictionary<string, ServiceDeploymentSnapshot> deploymentByRevision,
        ServiceRevisionCatalogSnapshot? revisionCatalog)
    {
        ArgumentNullException.ThrowIfNull(target);
        var serviceKey = ServiceKeys.Build(identity);
        if (!deploymentByRevision.TryGetValue(target.RevisionId, out var deployment))
        {
            throw new InvalidOperationException(
                $"Active deployment for '{serviceKey}' revision '{target.RevisionId}' was not found.");
        }

        var artifact = revisionCatalog.GetRequiredPreparedArtifact(identity, target.RevisionId);

        var resolved = CloneTarget(target);
        resolved.DeploymentId = deployment.DeploymentId;
        resolved.PrimaryActorId = deployment.PrimaryActorId;
        resolved.AllocationWeight = resolved.AllocationWeight == 0 ? 100 : resolved.AllocationWeight;
        resolved.ServingState = resolved.ServingState == ServiceServingState.Unspecified
            ? ServiceServingState.Active
            : resolved.ServingState;
        if (resolved.EnabledEndpointIds.Count == 0)
            resolved.EnabledEndpointIds.Add(artifact.Endpoints.Select(x => x.EndpointId));

        return resolved;
    }

    private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState,
            EnabledEndpointIds = { source.EnabledEndpointIds },
        };
}
