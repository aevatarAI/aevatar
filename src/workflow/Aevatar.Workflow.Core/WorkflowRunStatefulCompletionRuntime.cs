using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunStatefulCompletionRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly WorkflowInternalStepDispatchHandler _dispatchInternalStepAsync;
    private readonly Func<WorkflowWhileState, string, CancellationToken, Task> _dispatchWhileIterationAsync;
    private readonly Func<WorkflowWhileState, string, int, bool> _evaluateWhileCondition;

    public WorkflowRunStatefulCompletionRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        WorkflowInternalStepDispatchHandler dispatchInternalStepAsync,
        Func<WorkflowWhileState, string, CancellationToken, Task> dispatchWhileIterationAsync,
        Func<WorkflowWhileState, string, int, bool> evaluateWhileCondition)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _dispatchInternalStepAsync = dispatchInternalStepAsync ?? throw new ArgumentNullException(nameof(dispatchInternalStepAsync));
        _dispatchWhileIterationAsync = dispatchWhileIterationAsync ?? throw new ArgumentNullException(nameof(dispatchWhileIterationAsync));
        _evaluateWhileCondition = evaluateWhileCondition ?? throw new ArgumentNullException(nameof(evaluateWhileCondition));
    }

    public async Task<bool> TryHandleParallelCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        foreach (var (parentStepId, pending) in state.PendingParallelSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.VoteStepId) &&
                string.Equals(pending.VoteStepId, evt.StepId, StringComparison.Ordinal))
            {
                var voteNextState = state.Clone();
                voteNextState.PendingParallelSteps.Remove(parentStepId);
                voteNextState.StepExecutions.Remove(evt.StepId);
                await _persistStateAsync(voteNextState, ct);

                var voteCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = pending.WorkersSuccess && evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                    WorkerId = evt.WorkerId,
                };
                foreach (var (key, value) in evt.Metadata)
                    voteCompleted.Metadata[key] = value;
                voteCompleted.Metadata["parallel.used_vote"] = "true";
                voteCompleted.Metadata["parallel.vote_step_id"] = evt.StepId;
                voteCompleted.Metadata["parallel.workers_success"] = pending.WorkersSuccess.ToString();
                await _publishAsync(voteCompleted, EventDirection.Self, ct);
                return true;
            }

            if (!WorkflowRunSupport.TryGetParallelParent(evt.StepId, out var completionParent) ||
                !string.Equals(completionParent, parentStepId, StringComparison.Ordinal))
            {
                continue;
            }

            var next = state.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingParallelSteps[parentStepId].ChildResults.Add(WorkflowRunSupport.ToRecordedResult(evt));
            var collected = next.PendingParallelSteps[parentStepId].ChildResults.Count;
            if (collected < next.PendingParallelSteps[parentStepId].ExpectedCount)
            {
                await _persistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingParallelSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!string.IsNullOrWhiteSpace(next.PendingParallelSteps[parentStepId].VoteStepType))
            {
                var voteStepId = $"{parentStepId}_vote";
                next.PendingParallelSteps[parentStepId].VoteStepId = voteStepId;
                next.PendingParallelSteps[parentStepId].WorkersSuccess = allSuccess;
                await _persistStateAsync(next, ct);
                await _dispatchInternalStepAsync(
                    state.RunId,
                    parentStepId,
                    voteStepId,
                    next.PendingParallelSteps[parentStepId].VoteStepType,
                    merged,
                    string.Empty,
                    next.PendingParallelSteps[parentStepId].VoteParameters.ToDictionary(x => x.Key, x => x.Value),
                    ct);
                return true;
            }

            next.PendingParallelSteps.Remove(parentStepId);
            await _persistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = allSuccess,
                Output = merged,
            };
            completed.Metadata["parallel.used_vote"] = "false";
            await _publishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        return false;
    }

    public async Task<bool> TryHandleForEachCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        var parentStepId = WorkflowRunSupport.TryGetForEachParent(evt.StepId);
        if (parentStepId == null || !state.PendingForeachSteps.TryGetValue(parentStepId, out _))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingForeachSteps[parentStepId].ChildResults.Add(WorkflowRunSupport.ToRecordedResult(evt));
        if (next.PendingForeachSteps[parentStepId].ChildResults.Count < next.PendingForeachSteps[parentStepId].ExpectedCount)
        {
            await _persistStateAsync(next, ct);
            return true;
        }

        var results = next.PendingForeachSteps[parentStepId].ChildResults.ToList();
        next.PendingForeachSteps.Remove(parentStepId);
        await _persistStateAsync(next, ct);
        await _publishAsync(new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = state.RunId,
            Success = results.All(x => x.Success),
            Output = string.Join("\n---\n", results.Select(x => x.Output)),
        }, EventDirection.Self, ct);
        return true;
    }

    public async Task<bool> TryHandleMapReduceCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var state = _stateAccessor();
        foreach (var (parentStepId, pending) in state.PendingMapReduceSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.ReduceStepId) &&
                string.Equals(pending.ReduceStepId, evt.StepId, StringComparison.Ordinal))
            {
                var reduceNextState = state.Clone();
                reduceNextState.PendingMapReduceSteps.Remove(parentStepId);
                reduceNextState.StepExecutions.Remove(evt.StepId);
                await _persistStateAsync(reduceNextState, ct);

                var reduceCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                reduceCompleted.Metadata["map_reduce.phase"] = "reduce";
                await _publishAsync(reduceCompleted, EventDirection.Self, ct);
                return true;
            }

            var mapParent = WorkflowRunSupport.TryGetMapReduceParent(evt.StepId);
            if (!string.Equals(mapParent, parentStepId, StringComparison.Ordinal))
                continue;

            var next = state.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingMapReduceSteps[parentStepId].ChildResults.Add(WorkflowRunSupport.ToRecordedResult(evt));
            if (next.PendingMapReduceSteps[parentStepId].ChildResults.Count < next.PendingMapReduceSteps[parentStepId].MapCount)
            {
                await _persistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingMapReduceSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!allSuccess || string.IsNullOrWhiteSpace(next.PendingMapReduceSteps[parentStepId].ReduceType))
            {
                next.PendingMapReduceSteps.Remove(parentStepId);
                await _persistStateAsync(next, ct);
                await _publishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = allSuccess,
                    Output = merged,
                    Error = allSuccess ? string.Empty : "one or more map steps failed",
                }, EventDirection.Self, ct);
                return true;
            }

            var reduceInput = string.IsNullOrEmpty(next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix)
                ? merged
                : next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix.TrimEnd() + "\n\n" + merged;
            var reduceStepId = $"{parentStepId}_reduce";
            next.PendingMapReduceSteps[parentStepId].ReduceStepId = reduceStepId;
            await _persistStateAsync(next, ct);
            await _dispatchInternalStepAsync(
                state.RunId,
                parentStepId,
                reduceStepId,
                next.PendingMapReduceSteps[parentStepId].ReduceType,
                reduceInput,
                next.PendingMapReduceSteps[parentStepId].ReduceRole,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
            return true;
        }

        return false;
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
