using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceRolloutQueryReader : IServiceRolloutQueryReader
{
    private readonly IProjectionDocumentReader<ServiceRolloutReadModel, string> _documentReader;
    private readonly bool _enabled;

    public ServiceRolloutQueryReader(
        IProjectionDocumentReader<ServiceRolloutReadModel, string> documentReader,
        ServiceProjectionOptions? options = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceRolloutSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentReader.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceRolloutSnapshot(
            readModel.Id,
            readModel.RolloutId,
            readModel.DisplayName,
            readModel.Status,
            readModel.CurrentStageIndex,
            readModel.Stages
                .OrderBy(x => x.StageIndex)
                .Select(x => new ServiceRolloutStageSnapshot(
                    x.StageId,
                    x.StageIndex,
                    x.Targets.Select(ServiceProjectionMapping.ToServingTargetSnapshot).ToList()))
                .ToList(),
            readModel.BaselineTargets.Select(ServiceProjectionMapping.ToServingTargetSnapshot).ToList(),
            readModel.FailureReason,
            readModel.StartedAt,
            readModel.UpdatedAt);
    }
}
