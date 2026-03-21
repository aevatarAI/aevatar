using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceDeploymentCatalogQueryReader : IServiceDeploymentCatalogQueryReader
{
    private readonly IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> _documentReader;
    private readonly bool _enabled;

    public ServiceDeploymentCatalogQueryReader(
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> documentReader,
        ServiceProjectionOptions? options = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceDeploymentCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentReader.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceDeploymentCatalogSnapshot(
            readModel.Id,
            readModel.Deployments
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.DeploymentId, StringComparer.Ordinal)
                .Select(x => new ServiceDeploymentSnapshot(
                    x.DeploymentId,
                    x.RevisionId,
                    x.PrimaryActorId,
                    x.Status,
                    x.ActivatedAt,
                    x.UpdatedAt))
                .ToList(),
            readModel.UpdatedAt);
    }
}
