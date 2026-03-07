using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunAsyncPolicyRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowDefinition?> _compiledWorkflowAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly WorkflowStepDispatchHandler _dispatchWorkflowStepAsync;
    private readonly WorkflowFinalizeRunHandler _finalizeRunAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;

    public WorkflowRunAsyncPolicyRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowDefinition?> compiledWorkflowAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        WorkflowStepDispatchHandler dispatchWorkflowStepAsync,
        WorkflowFinalizeRunHandler finalizeRunAsync,
        WorkflowRunEffectDispatcher effectDispatcher)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _compiledWorkflowAccessor = compiledWorkflowAccessor ?? throw new ArgumentNullException(nameof(compiledWorkflowAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _dispatchWorkflowStepAsync = dispatchWorkflowStepAsync ?? throw new ArgumentNullException(nameof(dispatchWorkflowStepAsync));
        _finalizeRunAsync = finalizeRunAsync ?? throw new ArgumentNullException(nameof(finalizeRunAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
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

        if (WorkflowRunSupport.IsTimeoutError(evt.Error))
            return false;

        var state = _stateAccessor();
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
            await _persistStateAsync(next, ct);
            await _dispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, state.RunId, ct);
            return true;
        }

        next.PendingRetryBackoffs[step.Id] = new WorkflowPendingRetryBackoffState
        {
            StepId = step.Id,
            DelayMs = delayMs,
            NextAttempt = nextRetryCount + 1,
            SemanticGeneration = WorkflowRunSupport.NextSemanticGeneration(
                state.PendingRetryBackoffs.TryGetValue(step.Id, out var existingBackoff)
                    ? existingBackoff.SemanticGeneration
                    : 0),
        };
        await _persistStateAsync(next, ct);
        await _effectDispatcher.ScheduleWorkflowCallbackAsync(
            WorkflowRunSupport.BuildRetryBackoffCallbackId(state.RunId, step.Id),
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

        var workflow = _compiledWorkflowAccessor();
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
                await _persistStateAsync(next, ct);
                if (nextStep == null)
                {
                    await _finalizeRunAsync(true, output, string.Empty, ct);
                    return true;
                }

                await _dispatchWorkflowStepAsync(nextStep, output, _stateAccessor().RunId, ct);
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = workflow.GetStep(policy.FallbackStep);
                if (fallback == null)
                    return false;

                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                await _persistStateAsync(next, ct);
                await _dispatchWorkflowStepAsync(fallback, evt.Output ?? string.Empty, _stateAccessor().RunId, ct);
                return true;
            }
            default:
                return false;
        }
    }
}
