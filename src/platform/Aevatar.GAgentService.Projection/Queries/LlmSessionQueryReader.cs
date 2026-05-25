using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
// Refactor (iter81/cluster-081-direct-response-completion-not-session-fact):
//   Old pattern: direct Responses/Messages held terminal completion in request-local result; LlmSession only marked Completed
//   New principle: record typed LlmSessionCompletion on session for direct paths; terminal protocol output renders from session contract/readmodel
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
                .ToArray(),
            MapCompletion(readModel.Completion));

    // Refactor (iter75/cluster-075-responses-agui-host-completion-state):
    //   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
    //   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
    private static LlmSessionCompletionSnapshot? MapCompletion(LlmSessionCompletionReadModel? completion)
    {
        if (completion is null || completion.CompletedAt is null)
            return null;

        return new LlmSessionCompletionSnapshot(
            completion.OutputText ?? string.Empty,
            completion.ToolCalls
                .Select(static tool => new LlmSessionCompletedToolCallSnapshot(
                    tool.CallId,
                    tool.ToolName,
                    ResponsesJsonValues.ToBoundaryJson(tool.Result)))
                .ToArray(),
            completion.CompletedAt,
            string.IsNullOrWhiteSpace(completion.FailureCode) ? null : completion.FailureCode,
            string.IsNullOrWhiteSpace(completion.FailureMessage) ? null : completion.FailureMessage,
            completion.Usage is null
                ? null
                : new TokenUsage(
                    completion.Usage.PromptTokens,
                    completion.Usage.CompletionTokens,
                    completion.Usage.TotalTokens));
    }

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
