using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunStepDispatcher
{
    private readonly WorkflowRunReadContext _read;
    private readonly WorkflowRunWriteContext _write;
    private readonly Func<string, TimeSpan, Google.Protobuf.IMessage, int, string, string?, string, CancellationToken, Task> _scheduleWorkflowCallbackAsync;
    private readonly WorkflowRunStepRequestFactory _stepRequestFactory;
    private readonly WorkflowExpressionEvaluator _expressionEvaluator;

    public WorkflowRunStepDispatcher(
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        Func<string, TimeSpan, Google.Protobuf.IMessage, int, string, string?, string, CancellationToken, Task> scheduleWorkflowCallbackAsync,
        WorkflowRunStepRequestFactory stepRequestFactory,
        WorkflowExpressionEvaluator expressionEvaluator)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _scheduleWorkflowCallbackAsync = scheduleWorkflowCallbackAsync ?? throw new ArgumentNullException(nameof(scheduleWorkflowCallbackAsync));
        _stepRequestFactory = stepRequestFactory ?? throw new ArgumentNullException(nameof(stepRequestFactory));
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
    }

    public async Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct)
    {
        var state = _read.State;
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (_read.CompiledWorkflow?.Configuration.ClosedWorldMode == true &&
            WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonicalType))
        {
            await _write.PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step type '{canonicalType}' is blocked in closed_world_mode",
            }, EventDirection.Self, ct);
            return;
        }

        var request = _stepRequestFactory.BuildStepRequest(step, input, runId, state, _read.CompiledWorkflow);
        var next = state.Clone();
        next.ActiveStepId = step.Id;
        next.Status = "active";
        next.StepExecutions[step.Id] = _stepRequestFactory.BuildExecutionState(
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
                SemanticGeneration = WorkflowSemanticGeneration.Next(
                    state.PendingTimeouts.TryGetValue(step.Id, out var existingTimeout)
                        ? existingTimeout.SemanticGeneration
                        : 0),
            };
        }
        else
        {
            next.PendingTimeouts.Remove(step.Id);
        }

        await _write.PersistStateAsync(next, ct);

        if (step.TimeoutMs is > 0)
        {
            await _scheduleWorkflowCallbackAsync(
                WorkflowCallbackKeys.BuildStepTimeoutCallbackId(runId, step.Id),
                TimeSpan.FromMilliseconds(next.PendingTimeouts[step.Id].TimeoutMs),
                new WorkflowStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = step.Id,
                    TimeoutMs = next.PendingTimeouts[step.Id].TimeoutMs,
                },
                next.PendingTimeouts[step.Id].SemanticGeneration,
                step.Id,
                null,
                "step_timeout",
                ct);
        }

        try
        {
            await _write.PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishDispatchFailureAsync(step.Id, runId, $"step dispatch failed: {ex.Message}", ct);
        }
    }

    public async Task DispatchInternalStepAsync(
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

        var next = _read.State.Clone();
        next.StepExecutions[stepId] = _stepRequestFactory.BuildExecutionState(
            stepId,
            request.StepType,
            input,
            request.TargetRole,
            attempt: 1,
            parentStepId,
            request.Parameters);
        await _write.PersistStateAsync(next, ct);

        try
        {
            await _write.PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishDispatchFailureAsync(stepId, runId, $"internal step dispatch failed: {ex.Message}", ct);
        }
    }

    public Task DispatchWhileIterationAsync(
        WorkflowWhileState state,
        string input,
        CancellationToken ct)
    {
        var vars = _stepRequestFactory.BuildIterationVariables(input, state.Iteration, state.MaxIterations);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state.SubParameters)
            parameters[key] = _expressionEvaluator.Evaluate(value, vars);

        return DispatchInternalStepAsync(
            _read.RunId,
            state.StepId,
            $"{state.StepId}_iter_{state.Iteration}",
            state.SubStepType,
            input,
            state.SubTargetRole,
            parameters,
            ct);
    }

    public Task ScheduleCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        Google.Protobuf.IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct) =>
        _scheduleWorkflowCallbackAsync(callbackId, dueTime, evt, semanticGeneration, stepId, sessionId, kind, ct);

    private Task PublishDispatchFailureAsync(
        string stepId,
        string runId,
        string error,
        CancellationToken ct) =>
        _write.PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
}
