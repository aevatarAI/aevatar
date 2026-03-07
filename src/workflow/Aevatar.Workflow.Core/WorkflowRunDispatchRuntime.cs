using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunDispatchRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowDefinition?> _compiledWorkflowAccessor;
    private readonly WorkflowRunStepRequestFactory _stepRequestFactory;
    private readonly WorkflowExpressionEvaluator _expressionEvaluator;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;

    public WorkflowRunDispatchRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowDefinition?> compiledWorkflowAccessor,
        WorkflowRunStepRequestFactory stepRequestFactory,
        WorkflowExpressionEvaluator expressionEvaluator,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        WorkflowRunEffectDispatcher effectDispatcher)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _compiledWorkflowAccessor = compiledWorkflowAccessor ?? throw new ArgumentNullException(nameof(compiledWorkflowAccessor));
        _stepRequestFactory = stepRequestFactory ?? throw new ArgumentNullException(nameof(stepRequestFactory));
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
    }

    public async Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct)
    {
        var state = _stateAccessor();
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (_compiledWorkflowAccessor()?.Configuration.ClosedWorldMode == true &&
            WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonicalType))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step type '{canonicalType}' is blocked in closed_world_mode",
            }, EventDirection.Self, ct);
            return;
        }

        var request = _stepRequestFactory.BuildStepRequest(step, input, runId, state, _compiledWorkflowAccessor());
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
                SemanticGeneration = WorkflowRunSupport.NextSemanticGeneration(
                    state.PendingTimeouts.TryGetValue(step.Id, out var existingTimeout)
                        ? existingTimeout.SemanticGeneration
                        : 0),
            };
        }
        else
        {
            next.PendingTimeouts.Remove(step.Id);
        }

        await _persistStateAsync(next, ct);

        if (step.TimeoutMs is > 0)
        {
            await _effectDispatcher.ScheduleWorkflowCallbackAsync(
                WorkflowRunSupport.BuildStepTimeoutCallbackId(runId, step.Id),
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
            await _publishAsync(request, EventDirection.Self, ct);
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

        var next = _stateAccessor().Clone();
        next.StepExecutions[stepId] = _stepRequestFactory.BuildExecutionState(
            stepId,
            request.StepType,
            input,
            request.TargetRole,
            attempt: 1,
            parentStepId,
            request.Parameters);
        await _persistStateAsync(next, ct);

        try
        {
            await _publishAsync(request, EventDirection.Self, ct);
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
            _stateAccessor().RunId,
            state.StepId,
            $"{state.StepId}_iter_{state.Iteration}",
            state.SubStepType,
            input,
            state.SubTargetRole,
            parameters,
            ct);
    }

    private Task PublishDispatchFailureAsync(
        string stepId,
        string runId,
        string error,
        CancellationToken ct) =>
        _publishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
}
