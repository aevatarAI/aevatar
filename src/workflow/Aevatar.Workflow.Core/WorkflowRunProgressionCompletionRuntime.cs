using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunProgressionCompletionRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<WorkflowWhileState, string, CancellationToken, Task> _dispatchWhileIterationAsync;
    private readonly Func<WorkflowWhileState, string, int, bool> _evaluateWhileCondition;

    public WorkflowRunProgressionCompletionRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<WorkflowWhileState, string, CancellationToken, Task> dispatchWhileIterationAsync,
        Func<WorkflowWhileState, string, int, bool> evaluateWhileCondition)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _dispatchWhileIterationAsync = dispatchWhileIterationAsync ?? throw new ArgumentNullException(nameof(dispatchWhileIterationAsync));
        _evaluateWhileCondition = evaluateWhileCondition ?? throw new ArgumentNullException(nameof(evaluateWhileCondition));
    }

    public async Task<bool> TryHandleRaceCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        var parentStepId = WorkflowRunSupport.TryGetRaceParent(evt.StepId);
        if (parentStepId == null || !state.PendingRaceSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingRaceSteps[parentStepId].Received = pending.Received + 1;
        if (evt.Success && !pending.Resolved)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await _persistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = true,
                Output = evt.Output,
                WorkerId = evt.WorkerId,
            };
            completed.Metadata["race.winner"] = evt.StepId;
            await _publishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        if (next.PendingRaceSteps[parentStepId].Received >= pending.Total)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await _persistStateAsync(next, ct);
            if (!pending.Resolved)
            {
                await _publishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = false,
                    Error = "all race branches failed",
                }, EventDirection.Self, ct);
            }

            return true;
        }

        next.PendingRaceSteps[parentStepId].Resolved = pending.Resolved || evt.Success;
        await _persistStateAsync(next, ct);
        return true;
    }

    public async Task<bool> TryHandleWhileCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        var parentStepId = WorkflowRunSupport.TryGetWhileParent(evt.StepId);
        if (parentStepId == null || !state.PendingWhileSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        if (!evt.Success)
        {
            next.PendingWhileSteps.Remove(parentStepId);
            await _persistStateAsync(next, ct);
            await _publishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = false,
                Output = evt.Output,
                Error = evt.Error,
            }, EventDirection.Self, ct);
            return true;
        }

        var nextIteration = pending.Iteration + 1;
        if (nextIteration < pending.MaxIterations &&
            _evaluateWhileCondition(pending, evt.Output ?? string.Empty, nextIteration))
        {
            next.PendingWhileSteps[parentStepId].Iteration = nextIteration;
            await _persistStateAsync(next, ct);
            await _dispatchWhileIterationAsync(next.PendingWhileSteps[parentStepId], evt.Output ?? string.Empty, ct);
            return true;
        }

        next.PendingWhileSteps.Remove(parentStepId);
        await _persistStateAsync(next, ct);
        var completed = new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = state.RunId,
            Success = true,
            Output = evt.Output,
        };
        completed.Metadata["while.iterations"] = nextIteration.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.max_iterations"] = pending.MaxIterations.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.condition"] = pending.ConditionExpression;
        await _publishAsync(completed, EventDirection.Self, ct);
        return true;
    }

    public async Task<bool> TryHandleCacheCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        foreach (var (cacheKey, pending) in state.PendingCacheCalls)
        {
            if (!string.Equals(pending.ChildStepId, evt.StepId, StringComparison.Ordinal))
                continue;

            var next = state.Clone();
            next.PendingCacheCalls.Remove(cacheKey);
            next.StepExecutions.Remove(evt.StepId);
            if (evt.Success)
            {
                next.CacheEntries[cacheKey] = new WorkflowCacheEntry
                {
                    Value = evt.Output ?? string.Empty,
                    ExpiresAtUnixTimeMs = DateTimeOffset.UtcNow.AddSeconds(pending.TtlSeconds).ToUnixTimeMilliseconds(),
                };
            }
            await _persistStateAsync(next, ct);

            foreach (var waiter in pending.Waiters)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = waiter.ParentStepId,
                    RunId = state.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                completed.Metadata["cache.hit"] = "false";
                completed.Metadata["cache.key"] = WorkflowRunSupport.ShortenKey(cacheKey);
                await _publishAsync(completed, EventDirection.Self, ct);
            }

            return true;
        }

        return false;
    }
}
