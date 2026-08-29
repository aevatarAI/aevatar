using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvocationResolutionService : IServiceInvocationResolutionPort
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceInvocationCatalogQueryReader _invocationCatalogQueryReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly IServiceServingSetQueryReader _servingSetQueryReader;

    public ServiceInvocationResolutionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IServiceServingSetQueryReader servingSetQueryReader)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _invocationCatalogQueryReader = invocationCatalogQueryReader ?? throw new ArgumentNullException(nameof(invocationCatalogQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _servingSetQueryReader = servingSetQueryReader ?? throw new ArgumentNullException(nameof(servingSetQueryReader));
    }

    public async Task<bool> HasServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var definition = await _catalogQueryReader.GetAsync(identity, ct);
        return definition != null;
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
        var readiness = await ResolveReadinessAsync(request, definition, serviceKey, ct);
        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(request.Identity, ct);
        var artifact = ResolvePreparedArtifact(
            revisionCatalog,
            request.Identity,
            readiness.SelectedRevisionId,
            readiness);
        if (WorkflowServiceArtifactReadiness.RequiresCapabilityAdmissionRebind(artifact))
        {
            throw CreateUnavailable(
                readiness with
                {
                    UnavailableReason = ServiceInvokeUnavailableReason.PreparedArtifactIncompatible,
                    ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
                },
                $"Workflow service '{serviceKey}' endpoint '{request.EndpointId}' requires capability admission rebind before invoke.");
        }

        var endpoint = artifact.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            throw CreateUnavailable(
                readiness with
                {
                    UnavailableReason = ServiceInvokeUnavailableReason.PreparedArtifactMissing,
                    ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
                },
                $"Endpoint '{request.EndpointId}' was not found on prepared artifact for service '{serviceKey}'.");

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                serviceKey,
                readiness.SelectedRevisionId,
                readiness.SelectedDeploymentId,
                readiness.SelectedActorId,
                ServiceServingState.Active.ToString(),
                definition.PolicyIds),
            artifact,
            endpoint);
    }

    private async Task<ServiceInvokeReadinessSnapshot> ResolveReadinessAsync(
        ServiceInvocationRequest request,
        ServiceCatalogSnapshot definition,
        string serviceKey,
        CancellationToken ct)
    {
        var catalog = await _invocationCatalogQueryReader.GetAsync(request.Identity!, ct);
        if (catalog == null)
            throw CreateUnavailable(CreateUnspecifiedSnapshot(serviceKey, request.EndpointId), $"Service '{serviceKey}' has no invocation catalog readmodel.");

        var requestedRevisionId = request.RevisionId?.Trim() ?? string.Empty;
        var expectedRevisionId = string.IsNullOrWhiteSpace(requestedRevisionId)
            ? definition.DefaultServingRevisionId.Trim()
            : requestedRevisionId;
        var endpointEntries = catalog.Entries
            .Where(x => string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal))
            .ToArray();
        if (endpointEntries.Length == 0)
            throw CreateUnavailable(CreateUnspecifiedSnapshot(catalog, request.EndpointId), $"Endpoint '{request.EndpointId}' has no invocation readiness on service '{serviceKey}'.");

        var revisionEntries = string.IsNullOrWhiteSpace(expectedRevisionId)
            ? endpointEntries
            : endpointEntries
                .Where(x => string.Equals(x.SelectedRevisionId, expectedRevisionId, StringComparison.Ordinal))
                .ToArray();
        if (revisionEntries.Length == 0)
        {
            throw CreateServingTargetUnavailable(
                endpointEntries[0],
                serviceKey,
                request.EndpointId);
        }

        var servingSet = await _servingSetQueryReader.GetAsync(request.Identity!, ct);
        var readiness = revisionEntries.FirstOrDefault(entry =>
            servingSet?.Targets.Any(target => IsEligibleServingTarget(target, entry, request.EndpointId)) == true);
        if (readiness == null)
        {
            throw CreateServingTargetUnavailable(
                revisionEntries[0],
                serviceKey,
                request.EndpointId);
        }

        if (readiness.ReadinessStatus != ServiceInvokeReadinessStatus.Ready)
            throw CreateUnavailable(readiness, $"Service '{serviceKey}' endpoint '{request.EndpointId}' is not ready for invoke.");

        return readiness;
    }

    private static bool IsEligibleServingTarget(
        ServiceServingTargetSnapshot target,
        ServiceInvokeReadinessSnapshot readiness,
        string endpointId) =>
        string.Equals(target.RevisionId, readiness.SelectedRevisionId, StringComparison.Ordinal) &&
        string.Equals(target.DeploymentId, readiness.SelectedDeploymentId, StringComparison.Ordinal) &&
        string.Equals(target.PrimaryActorId, readiness.SelectedActorId, StringComparison.Ordinal) &&
        Enum.TryParse<ServiceServingState>(target.ServingState, ignoreCase: true, out var state) &&
        state == ServiceServingState.Active &&
        target.AllocationWeight > 0 &&
        (target.EnabledEndpointIds.Count == 0 ||
         target.EnabledEndpointIds.Any(x => string.Equals(x, endpointId, StringComparison.Ordinal)));

    private static ServiceInvokeReadinessException CreateServingTargetUnavailable(
        ServiceInvokeReadinessSnapshot readiness,
        string serviceKey,
        string endpointId) =>
        CreateUnavailable(
            readiness with
            {
                UnavailableReason = ServiceInvokeUnavailableReason.ServingTargetMissing,
                ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
            },
            $"Service '{serviceKey}' endpoint '{endpointId}' has no matching eligible serving target.");

    private static PreparedServiceRevisionArtifact ResolvePreparedArtifact(
        ServiceRevisionCatalogSnapshot? revisionCatalog,
        ServiceIdentity identity,
        string revisionId,
        ServiceInvokeReadinessSnapshot readiness)
    {
        try
        {
            return revisionCatalog.GetRequiredPreparedArtifact(identity, revisionId);
        }
        catch (InvalidOperationException ex)
        {
            throw CreateUnavailable(
                readiness with
                {
                    UnavailableReason = ServiceInvokeUnavailableReason.PreparedArtifactMissing,
                    ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
                },
                ex.Message);
        }
    }

    private static ServiceInvokeReadinessException CreateUnavailable(
        ServiceInvokeReadinessSnapshot snapshot,
        string message) =>
        new(message, snapshot);

    private static ServiceInvokeReadinessSnapshot CreateUnspecifiedSnapshot(
        ServiceInvocationCatalogSnapshot catalog,
        string endpointId) =>
        new(
            catalog.ServiceKey,
            endpointId,
            ServiceInvokeReadinessStatus.Unspecified,
            ServiceInvokeUnavailableReason.Unspecified,
            string.Empty,
            string.Empty,
            string.Empty,
            catalog.ObservedAt,
            catalog.AggregateStateVersion,
            catalog.LastEventId,
            catalog.SourceCatalogVersion,
            catalog.SourceServingVersion,
            catalog.SourceRevisionVersion);

    private static ServiceInvokeReadinessSnapshot CreateUnspecifiedSnapshot(
        string serviceKey,
        string endpointId) =>
        new(
            serviceKey,
            endpointId,
            ServiceInvokeReadinessStatus.Unspecified,
            ServiceInvokeUnavailableReason.Unspecified,
            string.Empty,
            string.Empty,
            string.Empty,
            DateTimeOffset.UnixEpoch,
            0,
            string.Empty,
            0,
            0,
            0);
}
