using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

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
