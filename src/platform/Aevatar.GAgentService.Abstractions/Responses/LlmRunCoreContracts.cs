using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Responses;

public sealed record LlmRunCoreRequest(
    LlmRunRequested Command,
    string RunId,
    string? OriginPlatform);

public sealed record LlmRunExecutionRequest(
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
    Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
        LlmStreamChunkObserved observed,
        CancellationToken ct = default);

    Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
        LlmToolCallObserved observed,
        CancellationToken ct = default);

    Task<LlmRunRecordDecision> RecordRunCompletedAsync(
        LlmRunCompleted completed,
        CancellationToken ct = default);

    Task<LlmRunRecordDecision> RecordRunFailedAsync(
        LlmRunFailed failed,
        CancellationToken ct = default);

    Task<LlmRunRecordDecision> RecordRunCancelledAsync(
        LlmRunCancelled cancelled,
        CancellationToken ct = default);
}

public readonly record struct LlmRunRecordDecision(bool Accepted, bool StopDispatching)
{
    public static LlmRunRecordDecision Continue { get; } = new(true, false);

    public static LlmRunRecordDecision Stop { get; } = new(false, true);
}

public interface ILlmRunExecutionService
{
    Task ExecuteAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default);
}

public interface ILlmRunExecutionScheduler
{
    ValueTask ScheduleAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default);
}

public interface ILlmRunExecutionTargetProvisioner
{
    Task<string> EnsureExecutionTargetAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default);
}
