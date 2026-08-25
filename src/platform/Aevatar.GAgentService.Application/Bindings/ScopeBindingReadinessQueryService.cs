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
    private readonly IServiceInvocationCatalogQueryReader _invocationCatalogQueryReader;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeBindingReadinessQueryService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceServingQueryPort serviceServingQueryPort,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceServingQueryPort = serviceServingQueryPort ?? throw new ArgumentNullException(nameof(serviceServingQueryPort));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _invocationCatalogQueryReader = invocationCatalogQueryReader
            ?? throw new ArgumentNullException(nameof(invocationCatalogQueryReader));
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
        Task<ServiceDeploymentCatalogSnapshot?>? deploymentCatalogTask = null;

        Task<ServiceDeploymentCatalogSnapshot?> GetDeploymentCatalogAsync() =>
            deploymentCatalogTask ??= _serviceLifecycleQueryPort
                .GetServiceDeploymentsAsync(identity, ct);

        async Task<ScopeBindingReadinessSnapshot> WithTerminalActivationFailureAsync(
            ScopeBindingReadinessSnapshot pendingSnapshot)
        {
            var failureCode = string.IsNullOrWhiteSpace(expectedRevisionId) ||
                              string.IsNullOrWhiteSpace(request.ExpectedActivationAttemptId)
                ? null
                : FindTerminalActivationFailureCode(
                    await GetDeploymentCatalogAsync().ConfigureAwait(false),
                    expectedRevisionId,
                    request.ExpectedActivationAttemptId);
            return pendingSnapshot with
            {
                TerminalActivationFailureCode = failureCode,
            };
        }

        var service = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct).ConfigureAwait(false);
        if (service == null)
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServiceCatalogMissing,
                ServiceCatalogVisible: false,
                ServingSetVisible: false,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var servingSet = await _serviceServingQueryPort.GetServiceServingSetAsync(identity, ct).ConfigureAwait(false);
        if (servingSet == null)
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServingSetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: false,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var serviceEndpointIds = expectedEndpointIds.Count > 0
            ? expectedEndpointIds
            : GetServiceEndpointIds(service);
        var eligibleTarget = FindEligibleServingTarget(servingSet, serviceEndpointIds, expectedRevisionId, expectedDeploymentId);
        if (eligibleTarget == null)
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.EligibleServingTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        if (!IsServiceCatalogTargetVisible(
                service,
                expectedEndpointIds,
                expectedRevisionId))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.ServiceCatalogTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var trafficView = await _serviceServingQueryPort.GetServiceTrafficViewAsync(identity, ct).ConfigureAwait(false);
        if (!IsTrafficViewTargetVisible(trafficView, serviceEndpointIds, expectedRevisionId, expectedDeploymentId))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.TrafficViewTargetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(identity, ct).ConfigureAwait(false);
        var artifact = FindPublishedPreparedArtifact(revisionCatalog, eligibleTarget.RevisionId);
        if (!DoesArtifactExposeEndpoints(artifact, serviceEndpointIds))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.PreparedArtifactMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var deploymentCatalog = await GetDeploymentCatalogAsync().ConfigureAwait(false);
        if (!IsDeploymentArtifactReady(
                deploymentCatalog,
                eligibleTarget,
                artifact!.ArtifactHash))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.PreparedArtifactMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: deploymentCatalog?.UpdatedAt ?? observedAtUtc)).ConfigureAwait(false);
        }

        if (WorkflowServiceArtifactReadiness.RequiresCapabilityAdmissionRebind(artifact!))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.InvocationCatalogNotReady,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: observedAtUtc)).ConfigureAwait(false);
        }

        var invocationCatalog = await _invocationCatalogQueryReader.GetAsync(identity, ct).ConfigureAwait(false);
        if (!IsInvocationCatalogReady(invocationCatalog, serviceEndpointIds, eligibleTarget))
        {
            return await WithTerminalActivationFailureAsync(new ScopeBindingReadinessSnapshot(
                normalizedScopeId,
                normalizedServiceId,
                ScopeBindingReadinessStatus.InvocationCatalogNotReady,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: false,
                RevisionId: eligibleTarget.RevisionId,
                DeploymentId: eligibleTarget.DeploymentId,
                ObservedAtUtc: invocationCatalog?.ObservedAt ?? observedAtUtc)).ConfigureAwait(false);
        }

        var ready = new ScopeBindingReadinessSnapshot(
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
        var observed = await WithTerminalActivationFailureAsync(ready).ConfigureAwait(false);
        return observed.TerminalActivationFailureCode == null
            ? observed
            : observed with
            {
                Status = ScopeBindingReadinessStatus.InvocationCatalogNotReady,
                InvokeReady = false,
            };
    }

    private static ServiceDeploymentActivationFailureCode? FindTerminalActivationFailureCode(
        ServiceDeploymentCatalogSnapshot? deploymentCatalog,
        string? expectedRevisionId,
        string? expectedActivationAttemptId)
    {
        if (string.IsNullOrWhiteSpace(expectedRevisionId) ||
            string.IsNullOrWhiteSpace(expectedActivationAttemptId))
            return null;

        var failure = deploymentCatalog?.ActivationFailures
            .Where(candidate => string.Equals(
                candidate.RevisionId,
                expectedRevisionId,
                StringComparison.Ordinal) &&
                string.Equals(
                    candidate.ActivationAttemptId,
                    expectedActivationAttemptId,
                    StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.OccurredAt)
            .FirstOrDefault();
        if (failure == null || failure.FailureCode == ServiceDeploymentActivationFailureCode.Unspecified)
            return null;

        return failure.FailureCode;
    }

    private static bool IsInvocationCatalogReady(
        ServiceInvocationCatalogSnapshot? catalog,
        IReadOnlyList<string> endpointIds,
        ServiceServingTargetSnapshot target)
    {
        if (catalog == null)
            return false;

        var targetEntries = catalog.Entries
            .Where(entry =>
                string.Equals(entry.SelectedRevisionId, target.RevisionId, StringComparison.Ordinal) &&
                string.Equals(entry.SelectedDeploymentId, target.DeploymentId, StringComparison.Ordinal))
            .ToArray();
        if (endpointIds.Count == 0)
        {
            return targetEntries.Length > 0 &&
                   targetEntries.All(entry => entry.ReadinessStatus == ServiceInvokeReadinessStatus.Ready);
        }

        return endpointIds.All(endpointId => targetEntries.Any(entry =>
            string.Equals(entry.EndpointId, endpointId, StringComparison.Ordinal) &&
            entry.ReadinessStatus == ServiceInvokeReadinessStatus.Ready));
    }

    private static IReadOnlyList<string> GetServiceEndpointIds(ServiceCatalogSnapshot service) =>
        NormalizeEndpointIds(service.Endpoints.Select(endpoint => endpoint.EndpointId));

    private static bool IsServiceCatalogTargetVisible(
        ServiceCatalogSnapshot service,
        IReadOnlyList<string> expectedEndpointIds,
        string? expectedRevisionId)
    {
        if (!string.IsNullOrWhiteSpace(expectedRevisionId) &&
            !string.Equals(service.DefaultServingRevisionId, expectedRevisionId, StringComparison.Ordinal))
        {
            return false;
        }

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

    private static PreparedServiceRevisionArtifact? FindPublishedPreparedArtifact(
        ServiceRevisionCatalogSnapshot? catalog,
        string revisionId) =>
        catalog.TryGetPublishedPreparedArtifact(revisionId, expectedArtifactHash: null, out var artifact)
            ? artifact
            : null;

    private static bool IsDeploymentArtifactReady(
        ServiceDeploymentCatalogSnapshot? catalog,
        ServiceServingTargetSnapshot target,
        string artifactHash) =>
        catalog?.Deployments.Any(deployment =>
            string.Equals(deployment.DeploymentId, target.DeploymentId, StringComparison.Ordinal) &&
            string.Equals(deployment.RevisionId, target.RevisionId, StringComparison.Ordinal) &&
            string.Equals(deployment.PrimaryActorId, target.PrimaryActorId, StringComparison.Ordinal) &&
            string.Equals(
                deployment.Status,
                ServiceDeploymentStatus.Active.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(deployment.ArtifactHash) &&
            string.Equals(deployment.ArtifactHash, artifactHash, StringComparison.Ordinal)) == true;

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
