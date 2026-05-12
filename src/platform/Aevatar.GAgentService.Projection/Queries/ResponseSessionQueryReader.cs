using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ResponseSessionQueryReader : IResponseSessionQueryPort
{
    private readonly IProjectionDocumentReader<ResponseSessionCurrentStateReadModel, string> _documentStore;
    private readonly bool _enabled;

    public ResponseSessionQueryReader(
        IProjectionDocumentReader<ResponseSessionCurrentStateReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ResponseSessionSnapshot?> GetByResponseIdAsync(
        string responseId,
        CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(responseId))
            return null;

        var direct = await _documentStore.GetAsync(ResponseSessionIds.BuildKey(responseId), ct);
        return direct == null ? null : Map(direct);
    }

    private static ResponseSessionSnapshot Map(ResponseSessionCurrentStateReadModel readModel) =>
        new(
            readModel.ResponseId,
            readModel.ScopeId,
            readModel.OwnerSubject,
            (ResponseSessionOriginKind)readModel.OriginKind,
            string.IsNullOrWhiteSpace(readModel.PreviousResponseId) ? null : readModel.PreviousResponseId,
            (ResponseSessionStatus)readModel.Status,
            readModel.CreatedAt,
            TimeSpan.FromSeconds(readModel.TtlSeconds),
            readModel.CancelledAt,
            readModel.ActorId,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.ForwardedToolCalls
                .Select(static call => new ResponseSessionForwardedToolCallSnapshot(
                    call.CallId,
                    call.ToolName,
                    call.SchemaHash,
                    call.ArgumentsJson,
                    (ResponseSessionForwardedToolCallStatus)call.Status,
                    call.Expiry,
                    string.IsNullOrWhiteSpace(call.ResultJson) ? null : call.ResultJson,
                    call.EmittedAt,
                    call.ReceivedAt,
                    call.ResolvedAt))
                .ToArray());
}
