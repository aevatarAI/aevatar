using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceTrafficViewQueryReader : IServiceTrafficViewQueryReader
{
    private readonly IProjectionDocumentReader<ServiceTrafficViewReadModel, string> _documentReader;

    public ServiceTrafficViewQueryReader(IProjectionDocumentReader<ServiceTrafficViewReadModel, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var readModel = await _documentReader.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceTrafficViewSnapshot(
            readModel.Id,
            readModel.Generation,
            readModel.ActiveRolloutId,
            readModel.Endpoints
                .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
                .Select(x => new ServiceTrafficEndpointSnapshot(
                    x.EndpointId,
                    x.Targets.Select(ServiceProjectionMapping.ToTrafficTargetSnapshot).ToList()))
                .ToList(),
            readModel.UpdatedAt);
    }
}
