using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Core.Services;

public sealed class ServiceInvokeReadinessEvaluator
{
    public IReadOnlyList<ServiceInvocationCatalogEntryState> Evaluate(
        IReadOnlyList<ServiceEndpointDescriptor> endpoints,
        IReadOnlyList<ServiceServingTargetSpec> servingTargets,
        IReadOnlyDictionary<string, ServiceRevisionRecordState> revisions) =>
        Evaluate(
            endpoints,
            servingTargets,
            ServiceInvocationCatalogCompaction.ProjectRevisions(revisions));

    public IReadOnlyList<ServiceInvocationCatalogEntryState> Evaluate(
        IReadOnlyList<ServiceEndpointDescriptor> endpoints,
        IReadOnlyList<ServiceServingTargetSpec> servingTargets,
        IReadOnlyDictionary<string, ServiceInvocationRevisionReadinessState> revisions)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(servingTargets);
        ArgumentNullException.ThrowIfNull(revisions);

        return endpoints
            .OrderBy(x => x.EndpointId ?? string.Empty, StringComparer.Ordinal)
            .Select(endpoint => EvaluateEndpoint(endpoint, servingTargets, revisions))
            .ToList();
    }

    private static ServiceInvocationCatalogEntryState EvaluateEndpoint(
        ServiceEndpointDescriptor endpoint,
        IReadOnlyList<ServiceServingTargetSpec> servingTargets,
        IReadOnlyDictionary<string, ServiceInvocationRevisionReadinessState> revisions)
    {
        var endpointId = endpoint.EndpointId?.Trim() ?? string.Empty;
        var selectedTarget = SelectTarget(endpointId, servingTargets);
        if (selectedTarget == null)
        {
            return Unavailable(
                endpointId,
                ServiceInvokeUnavailableReason.ServingTargetMissing);
        }

        if (!revisions.TryGetValue(selectedTarget.RevisionId ?? string.Empty, out var revision) ||
            revision.Status is not (ServiceRevisionStatus.Prepared or ServiceRevisionStatus.Published))
        {
            return Unavailable(
                endpointId,
                ServiceInvokeUnavailableReason.RevisionNotPrepared,
                selectedTarget);
        }

        if (string.IsNullOrWhiteSpace(revision.PreparedArtifactRevisionId) ||
            revision.PreparedEndpointIds.All(x => !string.Equals(x, endpointId, StringComparison.Ordinal)))
        {
            return Unavailable(
                endpointId,
                ServiceInvokeUnavailableReason.PreparedArtifactMissing,
                selectedTarget);
        }

        if (!revision.PreparedArtifactCompatible)
        {
            return Unavailable(
                endpointId,
                ServiceInvokeUnavailableReason.PreparedArtifactIncompatible,
                selectedTarget);
        }

        return new ServiceInvocationCatalogEntryState
        {
            EndpointId = endpointId,
            ReadinessStatus = ServiceInvokeReadinessStatus.Ready,
            UnavailableReason = ServiceInvokeUnavailableReason.Unspecified,
            SelectedRevisionId = selectedTarget.RevisionId ?? string.Empty,
            SelectedDeploymentId = selectedTarget.DeploymentId ?? string.Empty,
            SelectedActorId = selectedTarget.PrimaryActorId ?? string.Empty,
        };
    }

    private static ServiceServingTargetSpec? SelectTarget(
        string endpointId,
        IReadOnlyList<ServiceServingTargetSpec> servingTargets)
    {
        return servingTargets
            .Where(x =>
                x.AllocationWeight > 0 &&
                x.ServingState == ServiceServingState.Active &&
                IsEndpointEnabled(x, endpointId))
            .OrderByDescending(x => x.AllocationWeight)
            .ThenBy(x => x.RevisionId ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsEndpointEnabled(ServiceServingTargetSpec target, string endpointId)
    {
        if (target.EnabledEndpointIds.Count == 0)
            return true;

        return target.EnabledEndpointIds.Any(x => string.Equals(x, endpointId, StringComparison.Ordinal));
    }

    private static ServiceInvocationCatalogEntryState Unavailable(
        string endpointId,
        ServiceInvokeUnavailableReason reason,
        ServiceServingTargetSpec? target = null) =>
        new()
        {
            EndpointId = endpointId,
            ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
            UnavailableReason = reason,
            SelectedRevisionId = target?.RevisionId ?? string.Empty,
            SelectedDeploymentId = target?.DeploymentId ?? string.Empty,
            SelectedActorId = target?.PrimaryActorId ?? string.Empty,
        };
}
