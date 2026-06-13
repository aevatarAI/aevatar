using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Bindings;

public sealed class ScopeBindingReadinessQueryService : IScopeBindingReadinessQueryPort
{
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceServingQueryPort _serviceServingQueryPort;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeBindingReadinessQueryService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceServingQueryPort serviceServingQueryPort,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceServingQueryPort = serviceServingQueryPort ?? throw new ArgumentNullException(nameof(serviceServingQueryPort));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope workflow capability options are required.");
    }

    public async Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
        ScopeBindingReadinessRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ServiceId, nameof(request.ServiceId));
        var expectedRevisionId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.ExpectedRevisionId);
        var expectedDeploymentId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.ExpectedDeploymentId);
        var expectedEndpointIds = NormalizeEndpointIds(request.ExpectedEndpointIds);
        var identity = ScopeWorkflowCapabilityConventions.BuildServiceIdentity(
            _options,
            normalizedScopeId,
            normalizedServiceId,
            request.AppId);
        var observedAtUtc = DateTimeOffset.UtcNow;

        var service = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct).ConfigureAwait(false);
        if (service == null)
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServiceCatalogMissing,
                ServiceCatalogVisible: false,
                ServingSetVisible: false,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc);
        }

        var servingSet = await _serviceServingQueryPort.GetServiceServingSetAsync(identity, ct).ConfigureAwait(false);
        if (servingSet == null)
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServingSetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: false,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc);
        }

        var serviceEndpointIds = expectedEndpointIds.Count > 0
            ? expectedEndpointIds
            : GetServiceEndpointIds(service);
        var eligibleTarget = FindEligibleServingTarget(servingSet, serviceEndpointIds, expectedRevisionId, expectedDeploymentId);
        if (eligibleTarget == null)
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.EligibleServingTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc);
        }

        if (!IsServiceCatalogTargetVisible(service, expectedEndpointIds))
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServiceCatalogTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc);
        }

        var trafficView = await _serviceServingQueryPort.GetServiceTrafficViewAsync(identity, ct).ConfigureAwait(false);
        if (!IsTrafficViewTargetVisible(trafficView, serviceEndpointIds, expectedRevisionId, expectedDeploymentId))
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.TrafficViewTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc);
        }

        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(identity, ct).ConfigureAwait(false);
        var artifact = FindPreparedArtifact(revisionCatalog, eligibleTarget.RevisionId);
        if (!DoesArtifactExposeEndpoints(artifact, serviceEndpointIds))
        {
            return new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.PreparedArtifactMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc);
        }

        return new ScopeBindingReadinessSnapshot(
            normalizedScopeId,
            normalizedServiceId,
            ScopeBindingReadinessStatus.Ready,
            ServiceCatalogVisible: true,
            ServingSetVisible: true,
            EligibleServingTargetVisible: true,
            InvokeReady: true,
            RevisionId: eligibleTarget.RevisionId,
            DeploymentId: eligibleTarget.DeploymentId,
            ObservedAtUtc: observedAtUtc);
    }

    private static IReadOnlyList<string> GetServiceEndpointIds(ServiceCatalogSnapshot service) =>
        NormalizeEndpointIds(service.Endpoints.Select(endpoint => endpoint.EndpointId));

    private static bool IsServiceCatalogTargetVisible(
        ServiceCatalogSnapshot service,
        IReadOnlyList<string> expectedEndpointIds)
    {
        if (expectedEndpointIds.Count == 0)
            return true;

        var catalogEndpointIds = GetServiceEndpointIds(service);
        return expectedEndpointIds.All(expectedEndpointId => catalogEndpointIds.Contains(expectedEndpointId));
    }

    private static IReadOnlyList<string> NormalizeEndpointIds(IEnumerable<string>? endpointIds) =>
        endpointIds?
            .Select(endpointId => endpointId?.Trim())
            .Where(endpointId => !string.IsNullOrWhiteSpace(endpointId))
            .Select(endpointId => endpointId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static ServiceServingTargetSnapshot? FindEligibleServingTarget(
        ServiceServingSetSnapshot servingSet,
        IReadOnlyList<string> serviceEndpointIds,
        string? expectedRevisionId,
        string? expectedDeploymentId)
    {
        if (serviceEndpointIds.Count == 0)
        {
            return servingSet.Targets.FirstOrDefault(target =>
                IsEligibleServingTarget(target, expectedRevisionId, expectedDeploymentId, endpointId: null));
        }

        foreach (var endpointId in serviceEndpointIds)
        {
            var endpointTarget = servingSet.Targets.FirstOrDefault(target =>
                IsEligibleServingTarget(target, expectedRevisionId, expectedDeploymentId, endpointId));
            if (endpointTarget == null)
                return null;
        }

        return servingSet.Targets.FirstOrDefault(target =>
            IsEligibleServingTarget(target, expectedRevisionId, expectedDeploymentId, serviceEndpointIds[0]));
    }

    private static bool IsTrafficViewTargetVisible(
        ServiceTrafficViewSnapshot? trafficView,
        IReadOnlyList<string> serviceEndpointIds,
        string? expectedRevisionId,
        string? expectedDeploymentId)
    {
        if (trafficView == null)
            return true;

        var observedEndpointViews = trafficView.Endpoints
            .Where(endpoint => endpoint.Targets.Count > 0)
            .Where(endpoint => serviceEndpointIds.Count == 0 || serviceEndpointIds.Contains(endpoint.EndpointId))
            .ToList();
        return observedEndpointViews.Count == 0 || observedEndpointViews.All(endpoint => endpoint.Targets.Any(target =>
            IsEligibleTrafficTarget(target, expectedRevisionId, expectedDeploymentId)));
    }

    private static PreparedServiceRevisionArtifact? FindPreparedArtifact(
        ServiceRevisionCatalogSnapshot? catalog,
        string revisionId) =>
        catalog?.Revisions.FirstOrDefault(revision =>
            string.Equals(revision.RevisionId, revisionId, StringComparison.Ordinal))?.PreparedArtifact;

    private static bool DoesArtifactExposeEndpoints(
        PreparedServiceRevisionArtifact? artifact,
        IReadOnlyList<string> serviceEndpointIds)
    {
        if (artifact == null)
            return false;

        return serviceEndpointIds.Count == 0 || serviceEndpointIds.All(endpointId =>
            artifact.Endpoints.Any(endpoint => string.Equals(endpoint.EndpointId, endpointId, StringComparison.Ordinal)));
    }

    private static bool IsEligibleServingTarget(
        ServiceServingTargetSnapshot target,
        string? expectedRevisionId,
        string? expectedDeploymentId,
        string? endpointId) =>
        Enum.TryParse<ServiceServingState>(target.ServingState, ignoreCase: true, out var state)
        && state == ServiceServingState.Active
        && target.AllocationWeight > 0
        && (string.IsNullOrWhiteSpace(expectedRevisionId) || string.Equals(target.RevisionId, expectedRevisionId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(expectedDeploymentId) || string.Equals(target.DeploymentId, expectedDeploymentId, StringComparison.Ordinal))
        && IsEndpointEnabled(target, endpointId);

    private static bool IsEndpointEnabled(ServiceServingTargetSnapshot target, string? endpointId) =>
        string.IsNullOrWhiteSpace(endpointId)
        || target.EnabledEndpointIds.Count == 0
        || target.EnabledEndpointIds.Any(x => string.Equals(x, endpointId, StringComparison.Ordinal));

    private static bool IsEligibleTrafficTarget(
        ServiceTrafficTargetSnapshot target,
        string? expectedRevisionId,
        string? expectedDeploymentId) =>
        Enum.TryParse<ServiceServingState>(target.ServingState, ignoreCase: true, out var state)
        && state == ServiceServingState.Active
        && target.AllocationWeight > 0
        && (string.IsNullOrWhiteSpace(expectedRevisionId) || string.Equals(target.RevisionId, expectedRevisionId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(expectedDeploymentId) || string.Equals(target.DeploymentId, expectedDeploymentId, StringComparison.Ordinal));
}
