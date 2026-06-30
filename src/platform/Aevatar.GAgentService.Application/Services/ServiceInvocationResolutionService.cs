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

    public ServiceInvocationResolutionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _invocationCatalogQueryReader = invocationCatalogQueryReader ?? throw new ArgumentNullException(nameof(invocationCatalogQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
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
        var readiness = await ResolveReadinessAsync(request, serviceKey, ct);
        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(request.Identity, ct);
        var artifact = ResolvePreparedArtifact(
            revisionCatalog,
            request.Identity,
            readiness.SelectedRevisionId,
            readiness);
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
        string serviceKey,
        CancellationToken ct)
    {
        var catalog = await _invocationCatalogQueryReader.GetAsync(request.Identity!, ct);
        if (catalog == null)
            throw CreateUnavailable(CreateUnspecifiedSnapshot(serviceKey, request.EndpointId), $"Service '{serviceKey}' has no invocation catalog readmodel.");

        var requestedRevisionId = request.RevisionId?.Trim() ?? string.Empty;
        var readiness = catalog.Entries.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(requestedRevisionId) ||
             string.Equals(x.SelectedRevisionId, requestedRevisionId, StringComparison.Ordinal)));
        if (readiness == null)
            throw CreateUnavailable(CreateUnspecifiedSnapshot(catalog, request.EndpointId), $"Endpoint '{request.EndpointId}' has no invocation readiness on service '{serviceKey}'.");

        if (readiness.ReadinessStatus != ServiceInvokeReadinessStatus.Ready)
            throw CreateUnavailable(readiness, $"Service '{serviceKey}' endpoint '{request.EndpointId}' is not ready for invoke.");

        return readiness;
    }

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
