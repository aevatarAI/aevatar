using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponseSessionRegistrationPort
{
    Task<ResponseSessionRegistrationResult> RegisterAsync(
        ResponseSessionRecord record,
        CancellationToken ct = default);

    Task UpdateStatusAsync(
        string sessionActorId,
        string responseId,
        ResponseSessionStatus status,
        CancellationToken ct = default);

    Task RecordForwardedToolCallAsync(
        string sessionActorId,
        string responseId,
        ResponseSessionForwardedToolCall call,
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

public sealed record ResponseSessionRegistrationResult(string ActorId, string ResponseId);
