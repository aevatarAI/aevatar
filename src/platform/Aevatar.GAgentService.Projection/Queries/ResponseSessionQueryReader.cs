using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf;

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
                    PayloadToJsonString(call.ArgumentsPayload),
                    (ResponseSessionForwardedToolCallStatus)call.Status,
                    call.Expiry,
                    ResolveResultJson(call),
                    call.EmittedAt,
                    call.ReceivedAt,
                    call.ResolvedAt))
                .ToArray());

    private static string PayloadToJsonString(ByteString? payload) =>
        payload == null || payload.IsEmpty ? string.Empty : payload.ToStringUtf8();

    /// <summary>
    /// For Expired calls without a caller-provided result, the boundary
    /// synthesizes a <c>tool_call_expired</c> error envelope on read so the
    /// HTTP layer can return something concrete to the client. The actor
    /// itself never stored JSON.
    /// </summary>
    private static string? ResolveResultJson(ResponseSessionForwardedToolCallReadModel call)
    {
        if (call.ResultPayload != null && !call.ResultPayload.IsEmpty)
            return call.ResultPayload.ToStringUtf8();

        return (ResponseSessionForwardedToolCallStatus)call.Status == ResponseSessionForwardedToolCallStatus.Expired
            ? $$"""{"error":"tool_call_expired","call_id":"{{call.CallId}}"}"""
            : null;
    }
}
