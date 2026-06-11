using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class GAgentRunTerminalQueryReader : IGAgentRunTerminalQueryPort
{
    private readonly IProjectionDocumentReader<GAgentRunTerminalReadModel, string> _documentStore;
    private readonly bool _enabled;

    public GAgentRunTerminalQueryReader(
        IProjectionDocumentReader<GAgentRunTerminalReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
        string actorId,
        string correlationId,
        CancellationToken ct = default)
    {
        if (!_enabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        var readModel = await _documentStore.GetAsync(
            GAgentRunTerminalProjector.BuildDocumentId(actorId.Trim(), correlationId.Trim()),
            ct);
        return readModel == null ? null : Map(readModel);
    }

    public async Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!_enabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var result = await _documentStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(GAgentRunTerminalReadModel.ActorId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(actorId.Trim()),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(GAgentRunTerminalReadModel.SessionId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(sessionId.Trim()),
                    },
                ],
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(GAgentRunTerminalReadModel.ObservedAt),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                ],
            },
            ct);
        return result.Items.Select(Map).FirstOrDefault();
    }

    private static GAgentRunTerminalSnapshot Map(GAgentRunTerminalReadModel readModel) =>
        new(
            readModel.ActorId,
            readModel.SessionId,
            readModel.CorrelationId,
            (GAgentRunTerminalInteractionKind)readModel.InteractionKind,
            (GAgentRunTerminalStatus)readModel.Status,
            readModel.ReasonCode,
            readModel.ReasonMessage,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.ObservedAt);
}
