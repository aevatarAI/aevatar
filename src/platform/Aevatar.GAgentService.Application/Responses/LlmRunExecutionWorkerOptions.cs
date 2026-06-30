namespace Aevatar.GAgentService.Application.Responses;

// Host-configured bounds for the off-grain LLM run executor. The queue is the hand-off
// between the session actor's short scheduling turn and the background runner; it is a
// transient work queue, not a fact source (the run facts live on the session actor).
// Capacity and concurrency are per-host (every silo that can host the session actor
// registers the worker), not cluster-wide.
public sealed class LlmRunExecutionWorkerOptions
{
    public const string SectionName = "Aevatar:Responses:RunExecutionWorker";

    // Bounded hand-off queue depth. A full queue makes the scheduler throw
    // LlmRunExecutionQueueFullException (never block the actor turn), which the session
    // actor records as an execution_dispatch_failed terminal. Bounded on purpose so a
    // burst cannot grow process memory without limit.
    public int QueueCapacity { get; set; } = 1024;

    // Max runs executed concurrently per host. Each run is a long streaming I/O loop, so
    // this bounds upstream/provider fan-out per silo. Excess runs wait in the queue.
    public int MaxConcurrency { get; set; } = 64;

    // Grace period on host shutdown to let in-flight runs reach a terminal event before
    // the process exits. Runs not finished within the grace are covered by the session
    // actor's durable run-timeout finalizer (no silent hang).
    public int ShutdownDrainGraceSeconds { get; set; } = 10;

    public TimeSpan ShutdownDrainGrace =>
        ShutdownDrainGraceSeconds > 0 ? TimeSpan.FromSeconds(ShutdownDrainGraceSeconds) : TimeSpan.Zero;
}
