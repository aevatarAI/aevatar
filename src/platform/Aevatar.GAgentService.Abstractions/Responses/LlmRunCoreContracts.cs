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

// Off-grain hand-off queue between the session actor's short scheduling turn and the
// background run executor. Enqueue MUST be non-blocking so it never occupies the actor
// turn (epic #2271: the whole bug was holding a grain turn for the run). A full queue is
// surfaced as LlmRunExecutionQueueFullException so the caller can record a terminal
// failure instead of blocking. The worker that drains this queue runs the run loop off
// any Orleans turn.
public interface ILlmRunExecutionQueue
{
    void Enqueue(LlmRunExecutionRequest request);

    IAsyncEnumerable<LlmRunExecutionRequest> DequeueAllAsync(CancellationToken ct = default);
}

public sealed class LlmRunExecutionQueueFullException : Exception
{
    public LlmRunExecutionQueueFullException(string message)
        : base(message)
    {
    }
}
