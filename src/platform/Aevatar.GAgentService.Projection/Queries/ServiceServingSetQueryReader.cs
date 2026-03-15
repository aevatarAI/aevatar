using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceServingSetQueryReader : IServiceServingSetQueryReader
{
    private readonly IProjectionDocumentReader<ServiceServingSetReadModel, string> _documentReader;

    public ServiceServingSetQueryReader(IProjectionDocumentReader<ServiceServingSetReadModel, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var readModel = await _documentReader.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceServingSetSnapshot(
            readModel.Id,
            readModel.Generation,
            readModel.ActiveRolloutId,
            readModel.Targets.Select(ServiceProjectionMapping.ToServingTargetSnapshot).ToList(),
            readModel.UpdatedAt);
    }
}
