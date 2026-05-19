using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Workflows;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Bindings;

public sealed class ScopeBindingReadinessQueryService : IScopeBindingReadinessQueryPort
{
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceServingQueryPort _serviceServingQueryPort;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeBindingReadinessQueryService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceServingQueryPort serviceServingQueryPort,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceServingQueryPort = serviceServingQueryPort ?? throw new ArgumentNullException(nameof(serviceServingQueryPort));
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

        var eligibleTarget = servingSet.Targets.FirstOrDefault(target =>
            IsEligibleServingTarget(target, expectedRevisionId, expectedDeploymentId));
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

        var trafficView = await _serviceServingQueryPort.GetServiceTrafficViewAsync(identity, ct).ConfigureAwait(false);
        if (!IsTrafficViewTargetVisible(trafficView, service, expectedRevisionId, expectedDeploymentId))
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

    private static bool IsTrafficViewTargetVisible(
        ServiceTrafficViewSnapshot? trafficView,
        ServiceCatalogSnapshot service,
        string? expectedRevisionId,
        string? expectedDeploymentId)
    {
        if (trafficView == null)
            return true;

        var serviceEndpointIds = service.Endpoints
            .Select(endpoint => endpoint.EndpointId)
            .Where(endpointId => !string.IsNullOrWhiteSpace(endpointId))
            .ToHashSet(StringComparer.Ordinal);
        var observedEndpointViews = trafficView.Endpoints
            .Where(endpoint => endpoint.Targets.Count > 0)
            .Where(endpoint => serviceEndpointIds.Count == 0 || serviceEndpointIds.Contains(endpoint.EndpointId))
            .ToList();
        return observedEndpointViews.Count == 0 || observedEndpointViews.All(endpoint => endpoint.Targets.Any(target =>
            IsEligibleTrafficTarget(target, expectedRevisionId, expectedDeploymentId)));
    }

    private static bool IsEligibleServingTarget(
        ServiceServingTargetSnapshot target,
        string? expectedRevisionId,
        string? expectedDeploymentId) =>
        Enum.TryParse<ServiceServingState>(target.ServingState, ignoreCase: true, out var state)
        && state == ServiceServingState.Active
        && target.AllocationWeight > 0
        && (string.IsNullOrWhiteSpace(expectedRevisionId) || string.Equals(target.RevisionId, expectedRevisionId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(expectedDeploymentId) || string.Equals(target.DeploymentId, expectedDeploymentId, StringComparison.Ordinal));

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
