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

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    // Old pattern: catalog snapshots echoed active deployment fields stored on the catalog readmodel.
    // New principle: catalog snapshots expose definition facts only; callers use serving/deployment readmodels for runtime facts.
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
            ActiveServingRevisionId: string.Empty,
            DeploymentId: string.Empty,
            PrimaryActorId: string.Empty,
            DeploymentStatus: string.Empty,
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
            readModel.UpdatedAt,
            MapExternalExposure(readModel.ExternalExposure, readModel.StateVersion))
        {
            StateVersion = readModel.StateVersion,
            LastEventId = readModel.LastEventId ?? string.Empty,
        };
    }

    private static ServiceExternalExposureSnapshot? MapExternalExposure(
        ServiceCatalogExternalExposureReadModel? externalExposure,
        long sourceStateVersion)
    {
        if (externalExposure == null)
            return null;

        var nyxidSlug = externalExposure.NyxidSlug ?? string.Empty;
        var registeredAt = externalExposure.RegisteredAt;
        if (string.IsNullOrWhiteSpace(nyxidSlug) &&
            registeredAt == null &&
            externalExposure.Status == ServiceRegistrationStatus.Unspecified &&
            string.IsNullOrWhiteSpace(externalExposure.NyxidServiceId) &&
            string.IsNullOrWhiteSpace(externalExposure.DesiredSpecHash) &&
            string.IsNullOrWhiteSpace(externalExposure.LastError) &&
            externalExposure.Attempt == 0 &&
            externalExposure.NextAttemptAt == null &&
            !externalExposure.ExposureDesired)
        {
            return null;
        }

        return new ServiceExternalExposureSnapshot(
            nyxidSlug,
            registeredAt,
            externalExposure.Status,
            externalExposure.NyxidServiceId ?? string.Empty,
            externalExposure.DesiredSpecHash ?? string.Empty,
            externalExposure.RegisteredSpecHash ?? string.Empty,
            externalExposure.LastError ?? string.Empty,
            externalExposure.Attempt,
            externalExposure.NextAttemptAt,
            externalExposure.CredentialKid ?? string.Empty,
            externalExposure.ExposureDesired,
            sourceStateVersion);
    }
}
