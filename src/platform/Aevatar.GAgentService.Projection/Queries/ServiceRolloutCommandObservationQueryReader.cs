using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceRolloutCommandObservationQueryReader : IServiceRolloutCommandObservationQueryReader
{
    private readonly IProjectionDocumentReader<ServiceRolloutCommandObservationReadModel, string> _documentReader;
    private readonly bool _enabled;

    public ServiceRolloutCommandObservationQueryReader(
        IProjectionDocumentReader<ServiceRolloutCommandObservationReadModel, string> documentReader,
        ServiceProjectionOptions? options = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceRolloutCommandObservationSnapshot?> GetAsync(
        string commandId,
        CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(commandId))
            return null;

        var readModel = await _documentReader.GetAsync(commandId, ct);
        if (readModel == null)
            return null;

        return new ServiceRolloutCommandObservationSnapshot(
            readModel.CommandId,
            readModel.CorrelationId,
            readModel.ServiceKey,
            readModel.RolloutId,
            (ServiceRolloutStatus)readModel.Status,
            readModel.WasNoOp,
            readModel.StateVersion,
            readModel.ObservedAt);
    }
}
