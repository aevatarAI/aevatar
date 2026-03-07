using System.Globalization;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunProgressionCompletionRuntime
    : IWorkflowStatefulCompletionHandler
{
    private readonly WorkflowRunRuntimeContext _context;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;
    private readonly WorkflowRunStepRequestFactory _stepRequestFactory;

    public WorkflowRunProgressionCompletionRuntime(
        WorkflowRunRuntimeContext context,
        WorkflowRunDispatchRuntime dispatchRuntime,
        WorkflowRunStepRequestFactory stepRequestFactory)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatchRuntime = dispatchRuntime ?? throw new ArgumentNullException(nameof(dispatchRuntime));
        _stepRequestFactory = stepRequestFactory ?? throw new ArgumentNullException(nameof(stepRequestFactory));
    }

    public async Task<bool> TryHandleCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        if (await TryHandleRaceCompletionAsync(evt, ct))
            return true;
        if (await TryHandleWhileCompletionAsync(evt, ct))
            return true;

        return await TryHandleCacheCompletionAsync(evt, ct);
    }

    public async Task<bool> TryHandleRaceCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _context.State;
        var parentStepId = WorkflowRunSupport.TryGetRaceParent(evt.StepId);
        if (parentStepId == null || !state.PendingRaceSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingRaceSteps[parentStepId].Received = pending.Received + 1;
        if (evt.Success && !pending.Resolved)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await _context.PersistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = true,
                Output = evt.Output,
                WorkerId = evt.WorkerId,
            };
            completed.Metadata["race.winner"] = evt.StepId;
            await _context.PublishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        if (next.PendingRaceSteps[parentStepId].Received >= pending.Total)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await _context.PersistStateAsync(next, ct);
            if (!pending.Resolved)
            {
                await _context.PublishAsync(new StepCompletedEvent
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
        await _context.PersistStateAsync(next, ct);
        return true;
    }

    public async Task<bool> TryHandleWhileCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _context.State;
        var parentStepId = WorkflowRunSupport.TryGetWhileParent(evt.StepId);
        if (parentStepId == null || !state.PendingWhileSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        if (!evt.Success)
        {
            next.PendingWhileSteps.Remove(parentStepId);
            await _context.PersistStateAsync(next, ct);
            await _context.PublishAsync(new StepCompletedEvent
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
            _stepRequestFactory.EvaluateWhileCondition(pending, evt.Output ?? string.Empty, nextIteration))
        {
            next.PendingWhileSteps[parentStepId].Iteration = nextIteration;
            await _context.PersistStateAsync(next, ct);
            await _dispatchRuntime.DispatchWhileIterationAsync(next.PendingWhileSteps[parentStepId], evt.Output ?? string.Empty, ct);
            return true;
        }

        next.PendingWhileSteps.Remove(parentStepId);
        await _context.PersistStateAsync(next, ct);
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
        await _context.PublishAsync(completed, EventDirection.Self, ct);
        return true;
    }

    public async Task<bool> TryHandleCacheCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _context.State;
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
            await _context.PersistStateAsync(next, ct);

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
                await _context.PublishAsync(completed, EventDirection.Self, ct);
            }

            return true;
        }

        return false;
    }
}
