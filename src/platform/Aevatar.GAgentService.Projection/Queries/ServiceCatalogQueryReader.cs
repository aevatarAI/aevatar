using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceCatalogQueryReader : IServiceCatalogQueryReader
{
    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _documentStore;
    private readonly bool _enabled;

    public ServiceCatalogQueryReader(
        IProjectionDocumentReader<ServiceCatalogReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        return readModel == null ? null : Map(readModel);
    }

    public async Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(
        int take = 1000,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return [];

        var boundedTake = Math.Clamp(take, 1, 10_000);
        var result = await _documentStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        return result.Items.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var result = await _documentStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                Filters = new ProjectionDocumentFilter[]
                {
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.TenantId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(tenantId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.AppId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(appId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.Namespace),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(@namespace),
                    },
                },
            },
            ct);
        return result.Items
            .Take(boundedTake)
            .Select(Map)
            .ToList();
    }

    private static ServiceCatalogSnapshot Map(ServiceCatalogReadModel readModel)
    {
        return new ServiceCatalogSnapshot(
            readModel.Id,
            readModel.TenantId,
            readModel.AppId,
            readModel.Namespace,
            readModel.ServiceId,
            readModel.DisplayName,
            readModel.DefaultServingRevisionId,
            readModel.ActiveServingRevisionId,
            readModel.DeploymentId,
            readModel.PrimaryActorId,
            readModel.DeploymentStatus,
            readModel.Endpoints
                .Select(x => new ServiceEndpointSnapshot(
                    x.EndpointId,
                    x.DisplayName,
                    x.Kind,
                    x.RequestTypeUrl,
                    x.ResponseTypeUrl,
                    x.Description))
                .ToList(),
            [.. readModel.PolicyIds],
            readModel.UpdatedAt);
    }
}
