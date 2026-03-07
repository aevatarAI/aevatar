using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunFailurePolicyService
{
    private readonly WorkflowRunReadContext _read;
    private readonly WorkflowRunWriteContext _write;
    private readonly WorkflowRunStepDispatcher _dispatcher;
    private readonly Func<bool, string, string, CancellationToken, Task> _finalizeRunAsync;

    public WorkflowRunFailurePolicyService(
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunStepDispatcher dispatcher,
        Func<bool, string, string, CancellationToken, Task> finalizeRunAsync)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _finalizeRunAsync = finalizeRunAsync ?? throw new ArgumentNullException(nameof(finalizeRunAsync));
    }

    public async Task<bool> TryScheduleRetryAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(next);

        var policy = step.Retry;
        if (policy == null)
            return false;

        if (WorkflowCapabilityValueParsers.IsTimeoutError(evt.Error))
            return false;

        var state = _read.State;
        var scheduledRetryCount = state.RetryAttemptsByStepId.TryGetValue(step.Id, out var existingRetryCount)
            ? existingRetryCount
            : 0;
        var nextRetryCount = scheduledRetryCount + 1;
        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        if (nextRetryCount >= maxAttempts)
            return false;

        if (!state.StepExecutions.TryGetValue(step.Id, out var execution))
            return false;

        next.RetryAttemptsByStepId[step.Id] = nextRetryCount;
        var delayMs = string.Equals(policy.Backoff, "exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (nextRetryCount - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        if (delayMs <= 0)
        {
            await _write.PersistStateAsync(next, ct);
            await _dispatcher.DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, state.RunId, ct);
            return true;
        }

        next.PendingRetryBackoffs[step.Id] = new WorkflowPendingRetryBackoffState
        {
            StepId = step.Id,
            DelayMs = delayMs,
            NextAttempt = nextRetryCount + 1,
            SemanticGeneration = WorkflowSemanticGeneration.Next(
                state.PendingRetryBackoffs.TryGetValue(step.Id, out var existingBackoff)
                    ? existingBackoff.SemanticGeneration
                    : 0),
        };
        await _write.PersistStateAsync(next, ct);
        await _dispatcher.ScheduleCallbackAsync(
            WorkflowCallbackKeys.BuildRetryBackoffCallbackId(state.RunId, step.Id),
            TimeSpan.FromMilliseconds(delayMs),
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = state.RunId,
                StepId = step.Id,
                DelayMs = delayMs,
                NextAttempt = nextRetryCount + 1,
            },
            next.PendingRetryBackoffs[step.Id].SemanticGeneration,
            step.Id,
            sessionId: null,
            kind: "retry_backoff",
            ct);
        return true;
    }

    public async Task<bool> TryHandleOnErrorAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(next);

        var policy = step.OnError;
        if (policy == null)
            return false;

        var workflow = _read.CompiledWorkflow;
        if (workflow == null)
            return false;

        switch ((policy.Strategy ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "skip":
            {
                var output = policy.DefaultOutput ?? evt.Output ?? string.Empty;
                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                var nextStep = workflow.GetNextStep(step.Id);
                await _write.PersistStateAsync(next, ct);
                if (nextStep == null)
                {
                    await _finalizeRunAsync(true, output, string.Empty, ct);
                    return true;
                }

                await _dispatcher.DispatchWorkflowStepAsync(nextStep, output, _read.RunId, ct);
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = workflow.GetStep(policy.FallbackStep);
                if (fallback == null)
                    return false;

                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                await _write.PersistStateAsync(next, ct);
                await _dispatcher.DispatchWorkflowStepAsync(fallback, evt.Output ?? string.Empty, _read.RunId, ct);
                return true;
            }
            default:
                return false;
        }
    }
}
