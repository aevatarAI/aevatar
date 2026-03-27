using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowExecutionKernel : IEventModule<IEventHandlerContext>
{
    private const string ModuleStateKey = "workflow_execution_kernel";
    private static readonly Regex TimeoutErrorPattern = new(
        @"\bTIMEOUT\b|(?:^|[^A-Za-z0-9])timed out after\s+\d+\s*(?:ms|milliseconds?|s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly WorkflowDefinition _workflow;
    private readonly IWorkflowExecutionStateHost _stateHost;

    public WorkflowExecutionKernel(
        WorkflowDefinition workflow,
        IWorkflowExecutionStateHost stateHost)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _stateHost = stateHost ?? throw new ArgumentNullException(nameof(stateHost));
    }

    public string Name => "workflow_execution_kernel";
    public int Priority => 0;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor) ||
                payload.Is(WorkflowStoppedEvent.Descriptor) ||
                payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        var workflowContext = WorkflowExecutionContextAdapter.Create(ctx, _stateHost);
        var payload = envelope.Payload;
        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            await HandleStartWorkflowAsync(payload.Unpack<StartWorkflowEvent>(), workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            await HandleTimeoutFiredAsync(payload.Unpack<WorkflowStepTimeoutFiredEvent>(), envelope, workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            await HandleRetryBackoffFiredAsync(payload.Unpack<WorkflowStepRetryBackoffFiredEvent>(), envelope, workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowStoppedEvent.Descriptor))
        {
            await HandleWorkflowStoppedAsync(payload.Unpack<WorkflowStoppedEvent>(), workflowContext, ct);
            return;
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
            await HandleStepCompletedAsync(payload.Unpack<StepCompletedEvent>(), workflowContext, ct);
    }

    private async Task HandleStartWorkflowAsync(
        StartWorkflowEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = ResolveRunIdOrCurrent(evt.RunId, ctx);
        var state = LoadState(ctx);
        if (state.Active)
        {
            if (state.CurrentStepDispatchPending && IsActiveRun(state, runId))
            {
                await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
                return;
            }

            await PublishWorkflowCompletedAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = "workflow run is already active",
                },
                ct);
            return;
        }

        state.Active = true;
        state.RunId = runId;
        state.CurrentStepId = string.Empty;
        state.CurrentStepInput = string.Empty;
        state.Variables.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.TimeoutsByStepId.Clear();
        state.RetryBackoffsByStepId.Clear();
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.Variables["input"] = evt.Input ?? string.Empty;
        MergeStartParametersIntoVariables(state.Variables, evt.Parameters);
        await SaveStateAsync(state, ctx, ct);

        var entry = _workflow.Steps.FirstOrDefault();
        if (entry == null)
        {
            await CleanupRunAsync(state, ctx, ct);
            await PublishWorkflowCompletedAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = "无步骤",
                },
                ct);
            return;
        }

        await DispatchStepAsync(entry, evt.Input ?? string.Empty, state, ctx, ct);
    }

    private async Task HandleTimeoutFiredAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(evt.RunId);
        var stepId = evt.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
            return;

        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        if (!MatchesCurrentStep(state, stepId) && state.CurrentStepDispatchPending)
        {
            await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
            state = LoadState(ctx);
        }

        if (!MatchesCurrentStep(state, stepId))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore stale timeout run={RunId} step={StepId} expected={ExpectedStepId}",
                runId,
                stepId,
                state.CurrentStepId.Length == 0 ? "(none)" : state.CurrentStepId);
            return;
        }

        if (!MatchesCurrentStepTimeout(state, stepId, envelope))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore timeout without matching lease metadata run={RunId} step={StepId}",
                runId,
                stepId);
            return;
        }

        ctx.Logger.LogWarning("workflow_loop: step={StepId} timed out after {Ms}ms", stepId, evt.TimeoutMs);
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, TopologyAudience.Self, ct);

        state.TimeoutsByStepId.Remove(stepId);
        state.CurrentStepTimeoutCallbackId = string.Empty;
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleStepCompletedAsync(
        StepCompletedEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: ignore completion without run_id step={StepId}",
                evt.StepId);
            return;
        }

        var runId = NormalizeRunId(evt.RunId);
        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        if (!MatchesCurrentStep(state, evt.StepId) && state.CurrentStepDispatchPending)
        {
            await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
            state = LoadState(ctx);
        }

        var current = _workflow.GetStep(evt.StepId);
        if (current == null)
        {
            ctx.Logger.LogDebug("workflow_loop: ignore internal completion step={StepId}", evt.StepId);
            if (!string.IsNullOrWhiteSpace(evt.StepId))
            {
                state.Variables[evt.StepId] = evt.Output ?? string.Empty;
                await SaveStateAsync(state, ctx, ct);
            }

            return;
        }

        if (!MatchesCurrentStep(state, evt.StepId))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: ignore stale completion run={RunId} step={StepId} expected={ExpectedStepId}",
                runId,
                evt.StepId,
                state.CurrentStepId.Length == 0 ? "(none)" : state.CurrentStepId);
            return;
        }

        await CancelTimeoutAsync(state, evt.StepId, ctx, ct);
        if (!evt.Success && state.RetryBackoffsByStepId.ContainsKey(evt.StepId))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore duplicate failed completion while retry backoff is pending run={RunId} step={StepId}",
                runId,
                evt.StepId);
            return;
        }

        if (evt.Success)
            await CancelRetryBackoffAsync(state, evt.StepId, ctx, CancellationToken.None);

        var outputPreview = (evt.Output ?? string.Empty).Length > 200
            ? evt.Output![..200] + "..."
            : evt.Output ?? string.Empty;
        if (evt.Success)
        {
            ctx.Logger.LogInformation(
                "workflow_loop: step={StepId} completed success={Success} output=({Len} chars) {Preview}",
                evt.StepId,
                evt.Success,
                (evt.Output ?? string.Empty).Length,
                outputPreview);
        }
        else
        {
            ctx.Logger.LogError(
                "workflow_loop: step={StepId} failed run={RunId} error={Error} output=({Len} chars) {Preview}",
                evt.StepId,
                runId,
                string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                (evt.Output ?? string.Empty).Length,
                outputPreview);
        }

        if (!string.IsNullOrWhiteSpace(evt.AssignedVariable))
        {
            var assignValue = string.IsNullOrWhiteSpace(evt.AssignedValue)
                ? evt.Output ?? string.Empty
                : evt.AssignedValue;
            state.Variables[evt.AssignedVariable] = assignValue;
        }

        if (!string.IsNullOrWhiteSpace(evt.StepId))
            state.Variables[evt.StepId] = evt.Output ?? string.Empty;
        state.Variables["input"] = evt.Output ?? string.Empty;

        if (!evt.Success)
        {
            if (IsTimeoutError(evt.Error))
            {
                ctx.Logger.LogError(
                    "workflow_loop: run={RunId} step={StepId} timed out and run will fail. error={Error}",
                    runId,
                    evt.StepId,
                    evt.Error);
                await CleanupRunAsync(state, ctx, ct);
                await PublishWorkflowCompletedAsync(
                    ctx,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = false,
                        Error = evt.Error,
                    },
                    ct);
                return;
            }

            if (await TryRetryAsync(current, evt, state, ctx, ct))
                return;
            if (await TryOnErrorAsync(current, evt, state, ctx, ct))
                return;

            ctx.Logger.LogError(
                "workflow_loop: run={RunId} step={StepId} failed and no retry/on_error resolved. error={Error}",
                runId,
                evt.StepId,
                evt.Error);
            await CleanupRunAsync(state, ctx, ct);
            await PublishWorkflowCompletedAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = evt.Error,
                },
                ct);
            return;
        }

        state.RetryAttemptsByStepId.Remove(evt.StepId);
        state.RetryBackoffsByStepId.Remove(evt.StepId);
        await SaveStateAsync(state, ctx, ct);

        StepDefinition? next;
        if (!string.IsNullOrWhiteSpace(evt.NextStepId))
        {
            var directNextStepId = evt.NextStepId;
            next = _workflow.GetStep(directNextStepId);
            if (next == null)
            {
                ctx.Logger.LogError(
                    "workflow_loop: run={RunId} step={StepId} resolved invalid next_step={NextStepId}",
                    runId,
                    current.Id,
                    directNextStepId);
                await CleanupRunAsync(state, ctx, ct);
                await PublishWorkflowCompletedAsync(
                    ctx,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = false,
                        Error = $"invalid next_step '{directNextStepId}' from step '{current.Id}'",
                    },
                    ct);
                return;
            }
        }
        else
        {
            next = _workflow.GetNextStep(current.Id, evt.BranchKey);
        }

        if (next == null)
        {
            await CleanupRunAsync(state, ctx, ct);
            await PublishWorkflowCompletedAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = true,
                    Output = evt.Output,
                },
                ct);
            return;
        }

        await DispatchStepAsync(next, evt.Output ?? string.Empty, state, ctx, ct);
    }

    private async Task HandleWorkflowStoppedAsync(
        WorkflowStoppedEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(evt.RunId);
        if (string.IsNullOrWhiteSpace(runId))
            return;

        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        ctx.Logger.LogInformation(
            "workflow_loop: stopping run={RunId} reason={Reason}",
            runId,
            string.IsNullOrWhiteSpace(evt.Reason) ? "(none)" : evt.Reason);
        await CleanupRunAsync(state, ctx, ct);
    }

    private async Task<bool> TryRetryAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var policy = step.Retry;
        if (policy == null)
            return false;

        if (IsTimeoutError(evt.Error))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: step={StepId} timeout is not retried to avoid stale completion races",
                step.Id);
            return false;
        }

        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        var scheduledRetryCount = state.RetryAttemptsByStepId.GetValueOrDefault(step.Id, 0);
        var nextRetryCount = scheduledRetryCount + 1;
        if (nextRetryCount >= maxAttempts)
            return false;

        var nextAttemptNumber = nextRetryCount + 1;
        var delayMs = policy.Backoff.Equals("exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (nextRetryCount - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        ctx.Logger.LogWarning(
            "workflow_loop: step={StepId} retry attempt={Attempt}/{Max} delay={Delay}ms error={Error}",
            step.Id,
            nextAttemptNumber,
            maxAttempts,
            delayMs,
            evt.Error);

        var retryInput = state.CurrentStepInput;
        if (retryInput.Length == 0)
        {
            ctx.Logger.LogWarning(
                "workflow_loop: missing retry input run={RunId} step={StepId}, fallback to empty input",
                state.RunId,
                step.Id);
        }

        if (delayMs <= 0)
        {
            state.RetryAttemptsByStepId[step.Id] = nextRetryCount;
            await SaveStateAsync(state, ctx, ct);
            await DispatchStepAsync(step, retryInput, state, ctx, ct);
            return true;
        }

        await StartRetryBackoffAsync(state, step.Id, delayMs, nextAttemptNumber, ctx, ct);
        state.RetryAttemptsByStepId[step.Id] = nextRetryCount;
        await SaveStateAsync(state, ctx, ct);
        return true;
    }

    private async Task HandleRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(evt.RunId);
        var stepId = evt.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
            return;

        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        if (!MatchesCurrentStep(state, stepId) && state.CurrentStepDispatchPending)
        {
            await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
            state = LoadState(ctx);
        }

        if (!state.RetryBackoffsByStepId.TryGetValue(stepId, out var pending))
            return;

        if (!MatchesRetryBackoff(state, stepId, pending, envelope))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore retry backoff without matching lease metadata run={RunId} step={StepId}",
                runId,
                stepId);
            return;
        }

        if (pending.DispatchPending)
        {
            ctx.Logger.LogDebug(
                "workflow_loop: consume retry backoff replay after redispatch run={RunId} step={StepId}",
                runId,
                stepId);

            if (state.CurrentStepDispatchPending && MatchesCurrentStep(state, stepId))
            {
                await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
            }

            state = LoadState(ctx);
            state.RetryBackoffsByStepId.Remove(stepId);
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        if (!MatchesCurrentStep(state, stepId))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore retry backoff for stale step run={RunId} step={StepId} expected={ExpectedStepId}",
                runId,
                stepId,
                state.CurrentStepId.Length == 0 ? "(none)" : state.CurrentStepId);
            return;
        }

        var step = _workflow.GetStep(stepId);
        if (step == null)
        {
            ctx.Logger.LogWarning(
                "workflow_loop: retry backoff fired but step definition not found run={RunId} step={StepId}",
                runId,
                stepId);
            return;
        }

        var retryInput = state.CurrentStepInput;
        ctx.Logger.LogWarning(
            "workflow_loop: retry backoff fired run={RunId} step={StepId} next_attempt={Attempt} delay_ms={DelayMs}",
            runId,
            stepId,
            pending.NextAttempt,
            evt.DelayMs);

        var timeoutCallbackId = step.TimeoutMs is > 0
            ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
            : string.Empty;

        pending.DispatchPending = true;
        state.RetryBackoffsByStepId[stepId] = pending;
        state.CurrentStepDispatchPending = true;
        state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
        await SaveStateAsync(state, ctx, ct);

        try
        {
            await DispatchStepAsync(step, retryInput, state, ctx, ct);
        }
        catch
        {
            // Keep the backoff lease until redispatch succeeds so the same fired event can be replayed.
            await SaveStateAsync(state, ctx, CancellationToken.None);
            throw;
        }

        state.RetryBackoffsByStepId.Remove(stepId);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task StartRetryBackoffAsync(
        WorkflowExecutionKernelState state,
        string stepId,
        int delayMs,
        int nextAttempt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await CancelRetryBackoffAsync(state, stepId, ctx, CancellationToken.None);

        var callbackId = BuildStepRetryBackoffCallbackId(state.RunId, stepId, ResolveInboundEnvelopeId(ctx));
        state.RetryBackoffsByStepId[stepId] = new RetryBackoffState
        {
            Lease = null,
            NextAttempt = nextAttempt,
            DelayMs = delayMs,
            CallbackId = callbackId,
            DispatchPending = false,
        };
        await SaveStateAsync(state, ctx, ct);

        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(delayMs),
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = state.RunId,
                StepId = stepId,
                DelayMs = delayMs,
                NextAttempt = nextAttempt,
            },
            ct: ct);

        state.RetryBackoffsByStepId[stepId] = new RetryBackoffState
        {
            Lease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease),
            NextAttempt = nextAttempt,
            DelayMs = delayMs,
            CallbackId = callbackId,
            DispatchPending = false,
        };
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task<bool> TryOnErrorAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var policy = step.OnError;
        if (policy == null)
            return false;

        switch (policy.Strategy.ToLowerInvariant())
        {
            case "skip":
            {
                var output = policy.DefaultOutput ?? evt.Output ?? string.Empty;
                ctx.Logger.LogWarning(
                    "workflow_loop: step={StepId} failed, on_error=skip output=({Len} chars)",
                    step.Id,
                    output.Length);

                state.RetryAttemptsByStepId.Remove(step.Id);
                await SaveStateAsync(state, ctx, ct);

                var next = _workflow.GetNextStep(step.Id);
                if (next == null)
                {
                    await CleanupRunAsync(state, ctx, ct);
                    await PublishWorkflowCompletedAsync(
                        ctx,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = state.RunId,
                            Success = true,
                            Output = output,
                        },
                        ct);
                }
                else
                {
                    await DispatchStepAsync(next, output, state, ctx, ct);
                }

                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = _workflow.GetStep(policy.FallbackStep);
                if (fallback == null)
                    return false;

                ctx.Logger.LogWarning(
                    "workflow_loop: step={StepId} failed, on_error=fallback -> {Fallback}",
                    step.Id,
                    policy.FallbackStep);

                state.RetryAttemptsByStepId.Remove(step.Id);
                await SaveStateAsync(state, ctx, ct);
                var fallbackInput = string.IsNullOrWhiteSpace(evt.Output)
                    ? evt.Error ?? string.Empty
                    : evt.Output;
                await DispatchStepAsync(fallback, fallbackInput, state, ctx, ct);
                return true;
            }
            default:
                return false;
        }
    }

    private static async Task PublishWorkflowCompletedAsync(
        IWorkflowExecutionContext ctx,
        WorkflowCompletedEvent completed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(completed);

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        await ctx.PublishAsync(completed.Clone(), TopologyAudience.Parent, ct);
    }

    private async Task DispatchStepAsync(
        StepDefinition step,
        string input,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var request = BuildStepRequest(step, input, state, ctx);
        var timeoutCallbackId = step.TimeoutMs is > 0
            ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
            : string.Empty;

        state.CurrentStepId = step.Id;
        state.CurrentStepInput = input;
        state.CurrentStepDispatchPending = true;
        state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
        await SaveStateAsync(state, ctx, ct);

        RuntimeCallbackLease? timeoutLease = null;
        try
        {
            timeoutLease = await ScheduleStepTimeoutLeaseAsync(timeoutCallbackId, step, state.RunId, ctx, ct);
            if (timeoutLease != null)
            {
                state.TimeoutsByStepId[step.Id] = WorkflowRuntimeCallbackLeaseStateCodec.ToState(timeoutLease);
                await SaveStateAsync(state, ctx, ct);
            }

            await ctx.PublishAsync(request, TopologyAudience.Self, ct);
        }
        catch
        {
            if (timeoutLease != null)
            {
                await TryCancelLeaseAsync(timeoutLease, ctx, CancellationToken.None);
                state.TimeoutsByStepId.Remove(step.Id);
                await SaveStateAsync(state, ctx, CancellationToken.None);
            }

            throw;
        }

        state.CurrentStepDispatchPending = false;
        await SaveStateAsync(state, ctx, ct);
    }

    private static bool ShouldDeferWhileParameterEvaluation(string canonicalStepType, string parameterKey) =>
        string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(parameterKey, "condition", StringComparison.OrdinalIgnoreCase) ||
         parameterKey.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase));

    private static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        TimeoutErrorPattern.IsMatch(error);

    private async Task<RuntimeCallbackLease?> ScheduleStepTimeoutLeaseAsync(
        string callbackId,
        StepDefinition step,
        string runId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (step.TimeoutMs is not > 0 || string.IsNullOrWhiteSpace(callbackId))
            return null;

        var timeoutMs = Math.Clamp(step.TimeoutMs.Value, 100, 600_000);
        return await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(timeoutMs),
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = runId,
                StepId = step.Id,
                TimeoutMs = timeoutMs,
            },
            ct: ct);
    }

    private static async Task TryCancelLeaseAsync(
        RuntimeCallbackLease lease,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            await ctx.CancelDurableCallbackAsync(lease, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(
                ex,
                "workflow_loop: failed to cancel rolled-back timeout callback={CallbackId} generation={Generation}",
                lease.CallbackId,
                lease.Generation);
        }
    }

    private async Task CancelTimeoutAsync(
        WorkflowExecutionKernelState state,
        string stepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (MatchesCurrentStep(state, stepId))
            state.CurrentStepTimeoutCallbackId = string.Empty;

        if (!state.TimeoutsByStepId.Remove(stepId, out var lease))
        {
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, lease, ct);
    }

    private async Task CancelRetryBackoffAsync(
        WorkflowExecutionKernelState state,
        string stepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.RetryBackoffsByStepId.Remove(stepId, out var pending))
            return;

        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, pending.Lease, ct);
    }

    private async Task CleanupRunAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var timeoutLeases = state.TimeoutsByStepId.Values.ToList();
        var retryLeases = state.RetryBackoffsByStepId.Values.Select(x => x.Lease).ToList();

        state.Active = false;
        state.RunId = string.Empty;
        state.CurrentStepId = string.Empty;
        state.CurrentStepInput = string.Empty;
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.Variables.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.TimeoutsByStepId.Clear();
        state.RetryBackoffsByStepId.Clear();
        await SaveStateAsync(state, ctx, ct);

        foreach (var lease in timeoutLeases)
            await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, lease, ct);
        foreach (var lease in retryLeases)
            await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, lease, ct);
    }

    private static WorkflowExecutionKernelState LoadState(IWorkflowExecutionContext ctx) =>
        WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(ctx, ModuleStateKey);

    private static Task SaveStateAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.Active &&
            string.IsNullOrWhiteSpace(state.RunId) &&
            string.IsNullOrWhiteSpace(state.CurrentStepId) &&
            string.IsNullOrWhiteSpace(state.CurrentStepInput) &&
            !state.CurrentStepDispatchPending &&
            string.IsNullOrWhiteSpace(state.CurrentStepTimeoutCallbackId) &&
            state.Variables.Count == 0 &&
            state.RetryAttemptsByStepId.Count == 0 &&
            state.TimeoutsByStepId.Count == 0 &&
            state.RetryBackoffsByStepId.Count == 0)
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private static bool MatchesCurrentStep(WorkflowExecutionKernelState state, string? stepId) =>
        !string.IsNullOrWhiteSpace(stepId) &&
        string.Equals(state.CurrentStepId, stepId, StringComparison.Ordinal);

    private static bool IsActiveRun(WorkflowExecutionKernelState state, string runId) =>
        state.Active &&
        !string.IsNullOrWhiteSpace(runId) &&
        string.Equals(state.RunId, runId, StringComparison.Ordinal);

    private static string ResolveRunIdOrCurrent(string? runId, IWorkflowExecutionContext ctx)
    {
        var normalized = NormalizeRunId(runId);
        return string.IsNullOrWhiteSpace(normalized)
            ? NormalizeRunId(WorkflowExecutionStateAccess.GetRunId(ctx))
            : normalized;
    }

    private static string NormalizeRunId(string? runId) =>
        string.IsNullOrWhiteSpace(runId)
            ? string.Empty
            : WorkflowRunIdNormalizer.Normalize(runId);

    private static void MergeStartParametersIntoVariables(
        IDictionary<string, string> variables,
        Google.Protobuf.Collections.MapField<string, string> parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return;

        foreach (var (key, value) in parameters)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            variables[normalizedKey] = normalizedValue;
        }
    }

    private StepRequestEvent BuildStepRequest(
        StepDefinition step,
        string input,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx)
    {
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        var effectiveTargetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(_workflow, step);
        var inputPreview = input.Length > 200 ? input[..200] + "..." : input;
        ctx.Logger.LogInformation(
            "workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars) {Preview}",
            step.Id,
            canonicalStepType,
            string.IsNullOrWhiteSpace(effectiveTargetRole) ? "(none)" : effectiveTargetRole,
            input.Length,
            inputPreview);

        var request = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = canonicalStepType,
            RunId = state.RunId,
            Input = input,
            TargetRole = effectiveTargetRole,
        };

        state.Variables["input"] = input;
        foreach (var (key, value) in step.Parameters)
        {
            if (ShouldDeferWhileParameterEvaluation(canonicalStepType, key))
            {
                request.Parameters[key] = value;
                continue;
            }

            var evaluated = _expressionEvaluator.Evaluate(value, state.Variables);
            request.Parameters[key] = WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key)
                ? WorkflowPrimitiveCatalog.ToCanonicalType(evaluated)
                : evaluated;
        }

        if (step.Branches is { Count: > 0 })
        {
            foreach (var (branchKey, branchValue) in step.Branches)
                request.Parameters[$"branch.{branchKey}"] = branchValue;
        }

        if (!string.IsNullOrWhiteSpace(effectiveTargetRole) && _workflow != null)
        {
            var role = _workflow.Roles.FirstOrDefault(
                x => string.Equals(x.Id, effectiveTargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                request.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        return request;
    }

    private async Task ResumePendingCurrentStepDispatchAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.CurrentStepDispatchPending || string.IsNullOrWhiteSpace(state.CurrentStepId))
            return;

        var step = _workflow.GetStep(state.CurrentStepId);
        if (step == null)
        {
            state.CurrentStepDispatchPending = false;
            state.CurrentStepTimeoutCallbackId = string.Empty;
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        ctx.Logger.LogWarning(
            "workflow_loop: resuming pending dispatch run={RunId} step={StepId}",
            state.RunId,
            state.CurrentStepId);

        var request = BuildStepRequest(step, state.CurrentStepInput, state, ctx);
        RuntimeCallbackLease? timeoutLease = null;
        var createdTimeoutLease = false;
        try
        {
            if (!state.TimeoutsByStepId.ContainsKey(step.Id) &&
                !string.IsNullOrWhiteSpace(state.CurrentStepTimeoutCallbackId))
            {
                timeoutLease = await ScheduleStepTimeoutLeaseAsync(
                    state.CurrentStepTimeoutCallbackId,
                    step,
                    state.RunId,
                    ctx,
                    ct);
                if (timeoutLease != null)
                {
                    state.TimeoutsByStepId[step.Id] = WorkflowRuntimeCallbackLeaseStateCodec.ToState(timeoutLease);
                    createdTimeoutLease = true;
                    await SaveStateAsync(state, ctx, ct);
                }
            }

            await ctx.PublishAsync(request, TopologyAudience.Self, ct);
        }
        catch
        {
            if (createdTimeoutLease && timeoutLease != null)
            {
                await TryCancelLeaseAsync(timeoutLease, ctx, CancellationToken.None);
                state.TimeoutsByStepId.Remove(step.Id);
                await SaveStateAsync(state, ctx, CancellationToken.None);
            }

            throw;
        }

        state.CurrentStepDispatchPending = false;
        await SaveStateAsync(state, ctx, ct);
    }

    private static bool MatchesCurrentStepTimeout(
        WorkflowExecutionKernelState state,
        string stepId,
        EventEnvelope envelope)
    {
        if (state.TimeoutsByStepId.TryGetValue(stepId, out var expectedLease))
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, expectedLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, state.CurrentStepTimeoutCallbackId, StringComparison.Ordinal);
    }

    private static bool MatchesRetryBackoff(
        WorkflowExecutionKernelState state,
        string stepId,
        RetryBackoffState pending,
        EventEnvelope envelope)
    {
        _ = state;
        _ = stepId;
        if (pending.Lease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.Lease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.CallbackId, StringComparison.Ordinal);
    }

    private static string ResolveInboundEnvelopeId(IWorkflowExecutionContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.InboundEnvelope?.Id)
            ? Guid.NewGuid().ToString("N")
            : ctx.InboundEnvelope.Id;

    private static string BuildStepTimeoutCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-timeout", runId, stepId, originEnvelopeId);

    private static string BuildStepRetryBackoffCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-retry-backoff", runId, stepId, originEnvelopeId);

}
