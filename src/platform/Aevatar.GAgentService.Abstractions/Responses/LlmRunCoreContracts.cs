using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Responses;

public sealed record LlmRunCoreRequest(
    LlmRunRequested Command,
    string RunId,
    string? OriginPlatform);

public sealed record LlmRunExecutorRequest(
    string SessionActorId,
    string ResponseId,
    string RunId,
    LlmRunRequested Command,
    string? OriginPlatform);

public interface ILlmRunCore
{
    Task RunAsync(
        LlmRunCoreRequest request,
        ILlmRunSink sink,
        CancellationToken ct = default);
}

public interface ILlmRunSink
{
    Task RecordStreamChunkObservedAsync(
        LlmStreamChunkObserved observed,
        CancellationToken ct = default);

    Task RecordToolCallObservedAsync(
        LlmToolCallObserved observed,
        CancellationToken ct = default);

    Task RecordForwardedToolCallEmittedAsync(
        LlmSessionForwardedToolCallEmittedEvent emitted,
        CancellationToken ct = default);

    Task RecordRunCompletedAsync(
        LlmRunCompleted completed,
        CancellationToken ct = default);

    Task RecordRunFailedAsync(
        LlmRunFailed failed,
        CancellationToken ct = default);

    Task RecordRunCancelledAsync(
        LlmRunCancelled cancelled,
        CancellationToken ct = default);
}

public interface ILlmRunExecutionService
{
    Task ExecuteAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default);
}

public interface ILlmRunExecutionTargetProvisioner
{
    Task<string> EnsureExecutionTargetAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default);
}
