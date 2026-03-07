using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private async Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (_compiledWorkflow?.Configuration.ClosedWorldMode == true &&
            WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonicalType))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step type '{canonicalType}' is blocked in closed_world_mode",
            }, EventDirection.Self, ct);
            return;
        }

        var request = BuildStepRequest(step, input, runId);
        var next = State.Clone();
        next.ActiveStepId = step.Id;
        next.Status = StatusActive;
        next.StepExecutions[step.Id] = BuildExecutionState(
            step.Id,
            canonicalType,
            input,
            request.TargetRole,
            attempt: next.RetryAttemptsByStepId.TryGetValue(step.Id, out var retryAttempt) ? retryAttempt + 1 : 1,
            parentStepId: string.Empty,
            request.Parameters);
        if (step.TimeoutMs is > 0)
        {
            next.PendingTimeouts[step.Id] = new WorkflowPendingTimeoutState
            {
                StepId = step.Id,
                TimeoutMs = Math.Clamp(step.TimeoutMs.Value, 100, 600_000),
                SemanticGeneration = NextSemanticGeneration(
                    State.PendingTimeouts.TryGetValue(step.Id, out var existingTimeout)
                        ? existingTimeout.SemanticGeneration
                        : 0),
            };
        }
        else
        {
            next.PendingTimeouts.Remove(step.Id);
        }

        await PersistStateAsync(next, ct);

        if (step.TimeoutMs is > 0)
        {
            await ScheduleWorkflowCallbackAsync(
                BuildStepTimeoutCallbackId(runId, step.Id),
                TimeSpan.FromMilliseconds(next.PendingTimeouts[step.Id].TimeoutMs),
                new WorkflowStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = step.Id,
                    TimeoutMs = next.PendingTimeouts[step.Id].TimeoutMs,
                },
                next.PendingTimeouts[step.Id].SemanticGeneration,
                step.Id,
                sessionId: null,
                kind: "step_timeout",
                ct);
        }

        try
        {
            await PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step dispatch failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }

    private async Task DispatchInternalStepAsync(
        string runId,
        string parentStepId,
        string stepId,
        string stepType,
        string input,
        string targetRole,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var request = new StepRequestEvent
        {
            StepId = stepId,
            StepType = WorkflowPrimitiveCatalog.ToCanonicalType(stepType),
            RunId = runId,
            Input = input,
            TargetRole = targetRole ?? string.Empty,
        };
        foreach (var (key, value) in parameters)
            request.Parameters[key] = value;

        var next = State.Clone();
        next.StepExecutions[stepId] = BuildExecutionState(
            stepId,
            request.StepType,
            input,
            request.TargetRole,
            attempt: 1,
            parentStepId,
            request.Parameters);
        await PersistStateAsync(next, ct);

        try
        {
            await PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = $"internal step dispatch failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }

    private async Task DispatchWhileIterationAsync(
        WorkflowWhileState state,
        string input,
        CancellationToken ct)
    {
        var vars = BuildIterationVariables(input, state.Iteration, state.MaxIterations);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state.SubParameters)
            parameters[key] = _expressionEvaluator.Evaluate(value, vars);

        await DispatchInternalStepAsync(
            State.RunId,
            state.StepId,
            $"{state.StepId}_iter_{state.Iteration}",
            state.SubStepType,
            input,
            state.SubTargetRole,
            parameters,
            ct);
    }

    private async Task<bool> TryHandleSubWorkflowCompletionAsync(
        WorkflowCompletedEvent completed,
        string? publisherActorId,
        CancellationToken ct)
    {
        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId) || !State.PendingSubWorkflows.TryGetValue(childRunId, out var pending))
            return false;

        if (!string.IsNullOrWhiteSpace(pending.ChildActorId) &&
            !string.Equals(pending.ChildActorId, publisherActorId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Ignore workflow_call completion due to publisher mismatch childRun={ChildRunId} expected={Expected} actual={Actual}",
                childRunId,
                pending.ChildActorId,
                publisherActorId ?? "(none)");
            return true;
        }

        var next = State.Clone();
        next.PendingSubWorkflows.Remove(childRunId);
        await PersistStateAsync(next, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = State.RunId,
            Success = completed.Success,
            Output = completed.Output,
            Error = completed.Error,
        };
        parentCompleted.Metadata["workflow_call.invocation_id"] = pending.InvocationId;
        parentCompleted.Metadata["workflow_call.workflow_name"] = pending.WorkflowName;
        parentCompleted.Metadata["workflow_call.lifecycle"] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Metadata["workflow_call.child_actor_id"] = pending.ChildActorId;
        parentCompleted.Metadata["workflow_call.child_run_id"] = childRunId;
        await PublishAsync(parentCompleted, EventDirection.Self, ct);

        if (!string.Equals(WorkflowCallLifecycle.Normalize(pending.Lifecycle), WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pending.ChildActorId))
        {
            try
            {
                await _runtime.UnlinkAsync(pending.ChildActorId, ct);
                await _runtime.DestroyAsync(pending.ChildActorId, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean up child workflow actor {ChildActorId}", pending.ChildActorId);
            }
        }

        return true;
    }
}
