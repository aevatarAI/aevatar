using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceInvocationCatalogQueryReader : IServiceInvocationCatalogQueryReader
{
    private readonly IProjectionDocumentReader<ServiceInvocationCatalogReadModel, string> _documentStore;
    private readonly bool _enabled;

    public ServiceInvocationCatalogQueryReader(
        IProjectionDocumentReader<ServiceInvocationCatalogReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceInvocationCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceInvocationCatalogSnapshot(
            readModel.ServiceKey,
            readModel.Entries
                .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
                .Select(x => new ServiceInvokeReadinessSnapshot(
                    readModel.ServiceKey,
                    x.EndpointId,
                    x.ReadinessStatus,
                    x.UnavailableReason,
                    x.SelectedRevisionId,
                    x.SelectedDeploymentId,
                    x.SelectedActorId,
                    readModel.ObservedAt,
                    readModel.StateVersion,
                    readModel.LastEventId,
                    readModel.SourceCatalogVersion,
                    readModel.SourceServingVersion,
                    readModel.SourceRevisionVersion))
                .ToList(),
            readModel.ObservedAt,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.SourceCatalogVersion,
            readModel.SourceServingVersion,
            readModel.SourceRevisionVersion);
    }
}
