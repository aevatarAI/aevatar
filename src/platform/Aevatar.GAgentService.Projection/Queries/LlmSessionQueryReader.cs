using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class LlmSessionQueryReader : ILlmSessionQueryPort
{
    private readonly IProjectionDocumentReader<LlmSessionCurrentStateReadModel, string> _documentStore;
    private readonly bool _enabled;

    public LlmSessionQueryReader(
        IProjectionDocumentReader<LlmSessionCurrentStateReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<LlmSessionSnapshot?> GetByResponseIdAsync(
        string responseId,
        CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(responseId))
            return null;

        var direct = await _documentStore.GetAsync(LlmSessionIds.BuildKey(responseId), ct);
        return direct == null ? null : Map(direct);
    }

    private static LlmSessionSnapshot Map(LlmSessionCurrentStateReadModel readModel) =>
        new(
            readModel.ResponseId,
            readModel.ScopeId,
            readModel.OwnerSubject,
            (LlmSessionOriginKind)readModel.OriginKind,
            string.IsNullOrWhiteSpace(readModel.PreviousResponseId) ? null : readModel.PreviousResponseId,
            (LlmSessionStatus)readModel.Status,
            readModel.CreatedAt,
            TimeSpan.FromSeconds(readModel.TtlSeconds),
            readModel.CancelledAt,
            readModel.ActorId,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.ForwardedToolCalls
                .Select(static call => new LlmSessionForwardedToolCallSnapshot(
                    call.CallId,
                    call.ToolName,
                    call.SchemaHash,
                    ResponsesJsonValues.ToBoundaryJson(call.Arguments),
                    (LlmSessionForwardedToolCallStatus)call.Status,
                    call.Expiry,
                    ResolveResultJson(call),
                    call.EmittedAt,
                    call.ReceivedAt,
                    call.ResolvedAt))
                .ToArray());

    /// <summary>
    /// For Expired calls without a caller-provided result, the boundary
    /// synthesizes a <c>tool_call_expired</c> error envelope on read so the
    /// HTTP layer can return something concrete to the client. The actor
    /// itself never stored JSON.
    /// </summary>
    private static string? ResolveResultJson(LlmSessionForwardedToolCallReadModel call)
    {
        var resultJson = ResponsesJsonValues.ToBoundaryJson(call.Result);
        if (!string.IsNullOrWhiteSpace(resultJson))
            return resultJson;

        return (LlmSessionForwardedToolCallStatus)call.Status == LlmSessionForwardedToolCallStatus.Expired
            ? $$"""{"error":"tool_call_expired","call_id":"{{call.CallId}}"}"""
            : null;
    }
}
