using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public interface ILlmSessionRegistrationPort
{
    Task<LlmSessionRegistrationResult> RegisterAsync(
        LlmSessionRecord record,
        CancellationToken ct = default);

    Task UpdateStatusAsync(
        string sessionActorId,
        string responseId,
        LlmSessionStatus status,
        CancellationToken ct = default);

    Task RecordForwardedToolCallAsync(
        string sessionActorId,
        string responseId,
        LlmSessionForwardedToolCall call,
        CancellationToken ct = default);

    Task RecordCompletionAsync(
        string sessionActorId,
        string responseId,
        LlmSessionCompletion completion,
        CancellationToken ct = default);

    Task ReceiveForwardedToolResultAsync(
        string sessionActorId,
        string responseId,
        string callId,
        string schemaHash,
        string resultJson,
        CancellationToken ct = default);

    Task ResolveForwardedToolResultAsync(
        string sessionActorId,
        string responseId,
        string callId,
        CancellationToken ct = default);
}

public sealed record LlmSessionRegistrationResult(string ActorId, string ResponseId);
