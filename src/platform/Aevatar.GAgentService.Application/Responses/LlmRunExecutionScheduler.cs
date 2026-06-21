using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Application.Responses;

// Hands an LLM run off to the off-grain background executor. Called from the session
// actor's short scheduling turn (LlmSessionGAgent.TryDispatchTransientExecutionCommandAsync),
// so it MUST return immediately and never block: enqueueing is a non-blocking TryWrite. A
// full queue throws LlmRunExecutionQueueFullException, which the session actor records as an
// execution_dispatch_failed terminal. The actual run loop runs in LlmRunExecutionWorker, off
// any Orleans grain turn (epic #2271 root fix; replaces the prior per-run execution grain).
public sealed class LlmRunExecutionScheduler(ILlmRunExecutionQueue queue) : ILlmRunExecutionScheduler
{
    private readonly ILlmRunExecutionQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public ValueTask ScheduleAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        ct.ThrowIfCancellationRequested();

        // Defensive clone + trim: the request crosses from the actor turn to a worker thread.
        _queue.Enqueue(new LlmRunExecutionRequest(
            request.SessionActorId.Trim(),
            request.ResponseId.Trim(),
            request.RunId.Trim(),
            request.Command.Clone(),
            request.OriginPlatform));

        return ValueTask.CompletedTask;
    }
}
