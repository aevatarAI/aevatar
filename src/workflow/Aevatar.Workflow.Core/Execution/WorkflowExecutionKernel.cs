using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowExecutionKernel : IEventModule<IEventHandlerContext>
{
    private enum WorkflowStepDispatchKind
    {
        Forward,
        Compensation,
    }

    private const string ModuleStateKey = "workflow_execution_kernel";
    private const string WorkflowCallInvocationIdParameterKey = "workflow_call.invocation_id";
    private const int DefaultCompensationTimeoutMs = 30_000;
    private const int CompensationPhaseDeadlineMs = 300_000;
    private static readonly Regex TimeoutErrorPattern = new(
        @"\bTIMEOUT\b|(?:^|[^A-Za-z0-9])timed out after\s+\d+\s*(?:ms|milliseconds?|s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly WorkflowSideEffectIdempotencyKeyResolver _idempotencyKeyResolver;
    private readonly WorkflowDefinition _workflow;
    private readonly IWorkflowExecutionStateHost _stateHost;

    public WorkflowExecutionKernel(
        WorkflowDefinition workflow,
        IWorkflowExecutionStateHost stateHost)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _stateHost = stateHost ?? throw new ArgumentNullException(nameof(stateHost));
        _idempotencyKeyResolver = new WorkflowSideEffectIdempotencyKeyResolver(_expressionEvaluator);
    }

    public string Name => "workflow_execution_kernel";
    public int Priority => 0;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(CompensationRequestEvent.Descriptor) ||
                payload.Is(CompensationStepCompletedEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor) ||
                payload.Is(WorkflowStoppedEvent.Descriptor) ||
                payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowCompensationPhaseDeadlineFiredEvent.Descriptor) ||
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

        if (payload.Is(CompensationRequestEvent.Descriptor))
        {
            await HandleCompensationRequestAsync(payload.Unpack<CompensationRequestEvent>(), workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            await HandleTimeoutFiredAsync(payload.Unpack<WorkflowStepTimeoutFiredEvent>(), envelope, workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowCompensationPhaseDeadlineFiredEvent.Descriptor))
        {
            await HandleCompensationPhaseDeadlineFiredAsync(
                payload.Unpack<WorkflowCompensationPhaseDeadlineFiredEvent>(),
                envelope,
                workflowContext,
                ct);
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

        if (payload.Is(CompensationStepCompletedEvent.Descriptor))
        {
            await HandleCompensationStepCompletedAsync(payload.Unpack<CompensationStepCompletedEvent>(), workflowContext, ct);
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

            if (IsDuplicateWorkflowCallStart(state, evt, runId))
                return;

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
        state.CurrentStepInputFileRefs.Clear();
        state.InputFileRefs.Clear();
        state.Variables.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.TimeoutsByStepId.Clear();
        state.RetryBackoffsByStepId.Clear();
        state.ExecutionIdsByStepId.Clear();
        state.IdempotencyByStepId.Clear();
        state.CompensationExecutionIdsByStepId.Clear();
        state.Usage = new WorkflowUsageMetricsState();
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.CompensationPhaseDeadlineCallbackId = string.Empty;
        state.CompensationPhaseDeadlineLease = null;
        if (evt.WorkflowRuntime != null)
        {
            await _stateHost.UpdateExecutionContextAsync(
                WorkflowRunExecutionContextStateAccess.BuildWorkflowRuntimeDelta(evt.WorkflowRuntime),
                ct);
        }

        var forkSeed = evt.ForkSeed;
        var hasForkSeedStart = forkSeed != null && !string.IsNullOrWhiteSpace(forkSeed.StartAtStepId);
        if (hasForkSeedStart)
            MergeStartParametersIntoVariables(state.Variables, forkSeed!.Variables);
        ApplyForkSeedIdempotency(state, forkSeed);
        state.Variables["input"] = evt.Input ?? string.Empty;
        state.InputFileRefs.Add(evt.InputFileRefs.Select(static fileRef => fileRef.Clone()));
        MirrorRunUsageVariables(state);
        MergeStartParametersIntoVariables(state.Variables, evt.Parameters);
        await SaveStateAsync(state, ctx, ct);

        var entry = hasForkSeedStart
            ? _workflow.GetStep(forkSeed!.StartAtStepId)
            : _workflow.Steps.FirstOrDefault();
        if (entry == null)
        {
            await CleanupRunAsync(state, ctx, ct);
            var error = hasForkSeedStart
                ? $"fork seed start step '{forkSeed!.StartAtStepId}' was not found"
                : "无步骤";
            await PublishWorkflowCompletedAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = error,
                },
                ct);
            return;
        }

        var startInput = hasForkSeedStart && forkSeed!.Variables.TryGetValue("input", out var seedInput)
            ? seedInput ?? string.Empty
            : evt.Input ?? string.Empty;
        await DispatchStepAsync(entry, startInput, state.InputFileRefs, state, WorkflowStepDispatchKind.Forward, ctx, ct);
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
            FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
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

        try
        {
            // Idempotent execution: reject stale completions with mismatched execution_id
            if (!string.IsNullOrEmpty(evt.ExecutionId) &&
                state.ExecutionIdsByStepId.TryGetValue(evt.StepId, out var expectedExecutionId) &&
                !string.Equals(evt.ExecutionId, expectedExecutionId, StringComparison.Ordinal))
            {
                ctx.Logger.LogWarning(
                    "workflow_loop: reject stale execution_id run={RunId} step={StepId} expected={Expected} received={Received}",
                    runId, evt.StepId, expectedExecutionId, evt.ExecutionId);
                await ctx.PublishAsync(new StaleStepCompletionRejectedEvent
                {
                    StepId = evt.StepId,
                    RunId = runId,
                    ExpectedExecutionId = expectedExecutionId,
                    ReceivedExecutionId = evt.ExecutionId,
                }, TopologyAudience.Self, ct);
                return;
            }

            state.ExecutionIdsByStepId.Remove(evt.StepId);
            var compensationExecutionId = state.CompensationExecutionIdsByStepId.TryGetValue(evt.StepId, out var carriedCompensationExecutionId)
                ? carriedCompensationExecutionId
                : string.Empty;

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
            {
                await CancelRetryBackoffAsync(state, evt.StepId, ctx, CancellationToken.None);
            }

            // Do NOT log step output content: tool results routinely carry secrets
            // (NyxID access tokens, refresh tokens, connector credentials). Logging a
            // preview leaked partial credentials into stdout -> Elasticsearch. Length only.
            if (evt.Success)
            {
                ctx.Logger.LogInformation(
                    "workflow_loop: step={StepId} completed success={Success} output=({Len} chars)",
                    evt.StepId,
                    evt.Success,
                    (evt.Output ?? string.Empty).Length);
            }
            else
            {
                ctx.Logger.LogError(
                    "workflow_loop: step={StepId} failed run={RunId} error={Error} output=({Len} chars)",
                    evt.StepId,
                    runId,
                    string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                    (evt.Output ?? string.Empty).Length);
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
            ApplyStepUsage(evt, state);
            MirrorStepCompletionVariables(state, evt);

            if (!evt.Success)
            {
                if (!string.IsNullOrWhiteSpace(compensationExecutionId))
                {
                    if (await TryRetryAsync(current, evt, state, ctx, ct))
                        return;

                    await TryRecordFailedCompensationDeadLetterAsync(evt, compensationExecutionId, state, ctx, ct);
                    return;
                }

                if (IsTimeoutError(evt.Error))
                {
                    ctx.Logger.LogError(
                        "workflow_loop: run={RunId} step={StepId} timed out and run will fail. error={Error}",
                        runId,
                        evt.StepId,
                        evt.Error);
                    await TryStartCompensationOrPublishTerminalFailureAsync(
                        ctx,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = runId,
                            Success = false,
                            Error = evt.Error,
                        },
                        state,
                        evt,
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
                if (await TryRecordFailedCompensationDeadLetterAsync(evt, compensationExecutionId, state, ctx, ct))
                    return;

                await TryStartCompensationOrPublishTerminalFailureAsync(
                    ctx,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = false,
                        Error = evt.Error,
                    },
                    state,
                    evt,
                    ct);
                return;
            }

            state.RetryAttemptsByStepId.Remove(evt.StepId);
            state.RetryBackoffsByStepId.Remove(evt.StepId);
            await SaveStateAsync(state, ctx, ct);

            if (await TryRecordSuccessfulCompensationAsync(evt, compensationExecutionId, state, ctx, ct))
                return;

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
                    await TryStartCompensationOrPublishTerminalFailureAsync(
                        ctx,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = runId,
                            Success = false,
                            Error = $"invalid next_step '{directNextStepId}' from step '{current.Id}'",
                        },
                        state,
                        null,
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
                await CleanupRunAsync(state, ctx, ct, preserveTerminalFacts: true);
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

            await DispatchStepAsync(next, evt.Output ?? string.Empty, [], state, WorkflowStepDispatchKind.Forward, ctx, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.LogError(
                ex,
                "workflow_loop: completion handling failed run={RunId} step={StepId}",
                runId,
                evt.StepId);

            await TryStartCompensationOrPublishTerminalFailureAsync(
                ctx,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed(current, evt, ex),
                },
                state,
                evt,
                CancellationToken.None);
        }
    }

    private async Task HandleCompensationRequestAsync(
        CompensationRequestEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(evt.RunId);
        var compensationStepId = evt.CompensationStepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(compensationStepId))
            return;

        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        var step = _workflow.GetStep(compensationStepId);
        if (step == null)
        {
            await ctx.PublishAsync(new CompensationStepCompletedEvent
            {
                RunId = runId,
                CompensationStepId = compensationStepId,
                Success = false,
                Error = $"compensation step '{compensationStepId}' was not found",
                ExecutionId = evt.ExecutionId ?? string.Empty,
            }, TopologyAudience.Self, ct);
            return;
        }

        if (!state.IdempotencyByStepId.TryGetValue(compensationStepId, out var existing) ||
            string.IsNullOrWhiteSpace(existing.IdempotencyKey))
        {
            state.IdempotencyByStepId[compensationStepId] = new WorkflowStepIdempotencyState
            {
                LogicalRunId = runId,
                StepId = compensationStepId,
                LogicalAttempt = Math.Max(1, state.RetryAttemptsByStepId.GetValueOrDefault(compensationStepId, 0) + 1),
                IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            };
        }

        if (!string.IsNullOrWhiteSpace(evt.ExecutionId))
            state.CompensationExecutionIdsByStepId[compensationStepId] = evt.ExecutionId.Trim();

        await SaveStateAsync(state, ctx, ct);

        var input = string.IsNullOrWhiteSpace(evt.CapturedOutput)
            ? state.CurrentStepInput
            : evt.CapturedOutput;
        await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
        await DispatchStepAsync(step, input, state.CurrentStepInputFileRefs, state, WorkflowStepDispatchKind.Compensation, ctx, ct);
    }

    private async Task HandleCompensationStepCompletedAsync(
        CompensationStepCompletedEvent completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var result = await _stateHost.RecordCompensationStepCompletionAsync(completion, ct);
        await HandleCompensationTransitionAsync(
            result,
            completion.RunId,
            completion.CompensationStepId,
            completion.Error,
            ctx,
            ct);
    }

    private async Task<bool> TryRecordSuccessfulCompensationAsync(
        StepCompletedEvent evt,
        string compensationExecutionId,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var result = await _stateHost.RecordCompensationStepCompletionAsync(new CompensationStepCompletedEvent
        {
            RunId = evt.RunId,
            CompensationStepId = evt.StepId,
            Success = true,
            ExecutionId = compensationExecutionId ?? string.Empty,
        }, ct);
        if (result.Status != WorkflowCompensationTransitionStatus.NoCompensableLedger &&
            state.CompensationExecutionIdsByStepId.Remove(evt.StepId))
        {
            await SaveStateAsync(state, ctx, ct);
        }

        return await HandleCompensationTransitionAsync(
            result,
            evt.RunId,
            evt.StepId,
            evt.Error,
            ctx,
            ct);
    }

    private async Task<bool> TryRecordFailedCompensationDeadLetterAsync(
        StepCompletedEvent evt,
        string compensationExecutionId,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var result = await _stateHost.RecordCompensationStepCompletionAsync(new CompensationStepCompletedEvent
        {
            RunId = evt.RunId,
            CompensationStepId = evt.StepId,
            Success = false,
            Error = evt.Error ?? string.Empty,
            ExecutionId = compensationExecutionId ?? string.Empty,
        }, ct);
        if (result.Status != WorkflowCompensationTransitionStatus.NoCompensableLedger &&
            state.CompensationExecutionIdsByStepId.Remove(evt.StepId))
        {
            await SaveStateAsync(state, ctx, ct);
        }

        return await HandleCompensationTransitionAsync(
            result,
            evt.RunId,
            evt.StepId,
            evt.Error,
            ctx,
            ct);
    }

    private async Task<bool> HandleCompensationTransitionAsync(
        WorkflowCompensationTransitionResult result,
        string runId,
        string currentCompensationStepId,
        string? error,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        switch (result.Status)
        {
            case WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext:
            case WorkflowCompensationTransitionStatus.Started:
            case WorkflowCompensationTransitionStatus.AlreadyCompensating:
                await EnsureCompensationPhaseDeadlineAsync(LoadState(ctx), ctx, ct);
                await PublishCompensationRequestAsync(
                    ctx,
                    runId,
                    currentCompensationStepId,
                    result,
                    ct);
                return true;
            case WorkflowCompensationTransitionStatus.CompletedAll:
                await CleanupRunAsync(
                    LoadState(ctx),
                    ctx,
                    ct,
                    preserveTerminalFacts: true,
                    preserveCurrentStepInputVariable: true);
                await PublishWorkflowCompletedAsync(
                    ctx,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = NormalizeRunId(runId),
                        Success = false,
                        Error = error ?? string.Empty,
                    },
                    ct);
                return true;
            case WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate:
                return true;
            case WorkflowCompensationTransitionStatus.CompensationDeadLettered:
                {
                    var state = LoadState(ctx);
                    await CleanupRunAsync(
                        state,
                        ctx,
                        ct,
                        preserveTerminalFacts: true,
                        preserveCurrentStepInputVariable: true);
                    var deadLetterError = error ?? string.Empty;
                    await PublishWorkflowCompletedAsync(
                        ctx,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = NormalizeRunId(runId),
                            Success = false,
                            Error = deadLetterError,
                        },
                        ct);
                    return true;
                }
            case WorkflowCompensationTransitionStatus.NoCompensableLedger:
                return false;
            default:
                return false;
        }
    }

    private async Task TryStartCompensationOrPublishTerminalFailureAsync(
        IWorkflowExecutionContext ctx,
        WorkflowCompletedEvent terminalFailure,
        WorkflowExecutionKernelState state,
        StepCompletedEvent? terminalStep,
        CancellationToken ct)
    {
        var result = await _stateHost.TryStartCompensationAsync(terminalFailure, terminalStep, ct);
        switch (result.Status)
        {
            case WorkflowCompensationTransitionStatus.Started:
            case WorkflowCompensationTransitionStatus.AlreadyCompensating:
            case WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext:
                await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await PublishCompensationRequestAsync(
                    ctx,
                    terminalFailure.RunId,
                    state.CurrentStepId,
                    result,
                    ct);
                return;
            case WorkflowCompensationTransitionStatus.NoCompensableLedger:
                await TryCleanupRunForTerminalFailureAsync(
                    state,
                    ctx,
                    ct,
                    preserveTerminalFacts: true,
                    preserveCurrentStepInputVariable: true);
                await PublishWorkflowCompletedAsync(ctx, terminalFailure, ct);
                return;
            case WorkflowCompensationTransitionStatus.CompletedAll:
                await PublishWorkflowCompletedAsync(ctx, terminalFailure, ct);
                return;
            case WorkflowCompensationTransitionStatus.CompensationDeadLettered:
                await TryCleanupRunForTerminalFailureAsync(
                    state,
                    ctx,
                    ct,
                    preserveTerminalFacts: true,
                    preserveCurrentStepInputVariable: true);
                await PublishWorkflowCompletedAsync(ctx, terminalFailure, ct);
                return;
            case WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate:
            default:
                return;
        }
    }

    private async Task TryCleanupRunForTerminalFailureAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        bool preserveTerminalFacts = false,
        bool preserveCurrentStepInputVariable = false)
    {
        try
        {
            await CleanupRunAsync(
                state,
                ctx,
                ct,
                preserveTerminalFacts,
                preserveCurrentStepInputVariable);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.LogError(
                ex,
                "workflow_loop: terminal failure cleanup failed run={RunId} step={StepId}",
                state.RunId,
                state.CurrentStepId);
        }
    }

    private static Task PublishCompensationRequestAsync(
        IWorkflowExecutionContext ctx,
        string runId,
        string failedStepId,
        WorkflowCompensationTransitionResult result,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.NextCompensationStepId))
            return Task.CompletedTask;

        return ctx.PublishAsync(new CompensationRequestEvent
        {
            RunId = NormalizeRunId(runId),
            FailedStepId = string.IsNullOrWhiteSpace(result.FailedStepId)
                ? failedStepId ?? string.Empty
                : result.FailedStepId,
            CompensationStepId = result.NextCompensationStepId,
            IdempotencyKey = result.IdempotencyKey ?? string.Empty,
            CapturedOutput = result.CapturedOutput ?? string.Empty,
            ExecutionId = result.ExecutionId ?? string.Empty,
        }, TopologyAudience.Self, ct);
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
            state.IdempotencyByStepId.Remove(step.Id);
            state.RetryAttemptsByStepId[step.Id] = nextRetryCount;
            await SaveStateAsync(state, ctx, ct);
            await DispatchStepAsync(step, retryInput, state.CurrentStepInputFileRefs, state, ResolveRetryDispatchKind(state, step.Id), ctx, ct);
            return true;
        }

        await StartRetryBackoffAsync(state, step.Id, delayMs, nextAttemptNumber, ctx, ct);
        state.IdempotencyByStepId.Remove(step.Id);
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

        var dispatchKind = ResolveRetryDispatchKind(state, step.Id);
        var effectiveTimeoutMs = ResolveStepTimeoutMs(step, dispatchKind);
        var timeoutCallbackId = effectiveTimeoutMs > 0
            ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
            : string.Empty;

        pending.DispatchPending = true;
        state.RetryBackoffsByStepId[stepId] = pending;
        state.CurrentStepDispatchPending = true;
        state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
        await SaveStateAsync(state, ctx, ct);

        try
        {
            await DispatchStepAsync(step, retryInput, state.CurrentStepInputFileRefs, state, dispatchKind, ctx, ct);
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
                        await CleanupRunAsync(state, ctx, ct, preserveTerminalFacts: true);
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
                        await DispatchStepAsync(next, output, [], state, WorkflowStepDispatchKind.Forward, ctx, ct);
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
                    await DispatchStepAsync(fallback, fallbackInput, [], state, WorkflowStepDispatchKind.Forward, ctx, ct);
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
    }

    private async Task DispatchStepAsync(
        StepDefinition step,
        string input,
        IEnumerable<WorkflowFileRef> inputFileRefs,
        WorkflowExecutionKernelState state,
        WorkflowStepDispatchKind dispatchKind,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        RuntimeCallbackLease? timeoutLease = null;
        var requestPublishSucceeded = false;
        try
        {
            var fileRefs = inputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray();
            var request = BuildStepRequest(step, input, fileRefs, state, ctx);
            var idempotency = ResolveAndPersistStepIdempotency(step, state);
            request.IdempotencyKey = idempotency.IdempotencyKey;
            var effectiveTimeoutMs = ResolveStepTimeoutMs(step, dispatchKind);
            var timeoutCallbackId = effectiveTimeoutMs > 0
                ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
                : string.Empty;

            // Idempotent execution: generate unique execution_id per dispatch
            var executionId = Guid.NewGuid().ToString("N");
            request.ExecutionId = executionId;
            state.ExecutionIdsByStepId[step.Id] = executionId;

            state.CurrentStepId = step.Id;
            state.CurrentStepInput = input;
            state.CurrentStepInputFileRefs.Clear();
            state.CurrentStepInputFileRefs.Add(fileRefs.Select(static fileRef => fileRef.Clone()));
            state.CurrentStepDispatchPending = true;
            state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
            await SaveStateAsync(state, ctx, ct);

            timeoutLease = await ScheduleStepTimeoutLeaseAsync(timeoutCallbackId, step, effectiveTimeoutMs, state.RunId, ctx, ct);
            if (timeoutLease != null)
            {
                state.TimeoutsByStepId[step.Id] = WorkflowRuntimeCallbackLeaseStateCodec.ToState(timeoutLease);
                await SaveStateAsync(state, ctx, ct);
            }

            await ctx.PublishAsync(request, TopologyAudience.Self, ct);
            requestPublishSucceeded = true;
            await RecordCompensableStepDispatchAsync(step, idempotency, ct);

            state.CurrentStepDispatchPending = false;
            await SaveStateAsync(state, ctx, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (timeoutLease != null)
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    timeoutLease,
                    "workflow_loop rolled-back timeout cleanup",
                    CancellationToken.None);
                state.TimeoutsByStepId.Remove(step.Id);
                try
                {
                    await SaveStateAsync(state, ctx, CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    ctx.Logger.LogError(
                        saveEx,
                        "workflow_loop: failed to persist dispatch timeout cleanup run={RunId} step={StepId}",
                        state.RunId,
                        step.Id);
                }
            }

            await PublishStepDispatchTerminalFailureAsync(
                step,
                state,
                ctx,
                ex,
                requestPublishSucceeded,
                CancellationToken.None);
        }
    }

    private async Task PublishStepDispatchTerminalFailureAsync(
        StepDefinition step,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        Exception exception,
        bool requestPublishSucceeded,
        CancellationToken ct)
    {
        ctx.Logger.LogError(
            exception,
            "workflow_loop: step dispatch failed run={RunId} step={StepId}",
            state.RunId,
            step.Id);
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.TimeoutsByStepId.Remove(step.Id);
        try
        {
            await SaveStateAsync(state, ctx, ct);
        }
        catch (Exception saveEx) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.LogError(
                saveEx,
                "workflow_loop: failed to persist dispatch failure cleanup run={RunId} step={StepId}",
                state.RunId,
                step.Id);
        }

        var terminalFailure = new WorkflowCompletedEvent
        {
            WorkflowName = _workflow.Name,
            RunId = state.RunId,
            Success = false,
            Error = WorkflowRuntimeFailureMessages.StepDispatchFailed(step, exception),
        };
        if (!requestPublishSucceeded)
        {
            await TryStartCompensationOrPublishTerminalFailureAsync(
                ctx,
                terminalFailure,
                state,
                terminalStep: null,
                ct);
            return;
        }

        await TryStartCompensationOrPublishTerminalFailureAsync(
            ctx,
            terminalFailure,
            state,
            new StepCompletedEvent
            {
                RunId = state.RunId,
                StepId = step.Id,
                Success = false,
                FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                Error = WorkflowRuntimeFailureMessages.StepDispatchFailed(step, exception),
            },
            ct);
    }

    private async Task RecordCompensableStepDispatchAsync(
        StepDefinition step,
        WorkflowStepIdempotencyState idempotency,
        CancellationToken ct)
    {
        var compensationStepId = step.Compensation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compensationStepId) ||
            !WorkflowPrimitiveCatalog.IsSideEffectingPrimitive(step.Type))
        {
            return;
        }

        await _stateHost.RecordCompensableStepDispatchAsync(
            new CompensableStepDispatchedEvent
            {
                RunId = NormalizeRunId(_stateHost.RunId),
                StepId = step.Id,
                CompensationStepId = compensationStepId,
                IdempotencyKey = idempotency.IdempotencyKey ?? string.Empty,
                DispatchedAtUnixMs = 0,
            },
            ct);
    }

    private WorkflowStepIdempotencyState ResolveAndPersistStepIdempotency(
        StepDefinition step,
        WorkflowExecutionKernelState state)
    {
        if (state.IdempotencyByStepId.TryGetValue(step.Id, out var existing) &&
            !string.IsNullOrWhiteSpace(existing.IdempotencyKey))
        {
            var normalized = WorkflowSideEffectIdempotencyKeyResolver.NormalizeIdentity(existing);
            state.IdempotencyByStepId[step.Id] = normalized;
            return normalized;
        }

        var identity = new WorkflowStepIdempotencyState
        {
            LogicalRunId = state.RunId,
            StepId = step.Id,
            LogicalAttempt = Math.Max(1, state.RetryAttemptsByStepId.GetValueOrDefault(step.Id, 0) + 1),
        };
        var resolved = _idempotencyKeyResolver.Resolve(step, identity, state.Variables);
        state.IdempotencyByStepId[step.Id] = resolved;
        return resolved;
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
        int effectiveTimeoutMs,
        string runId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (effectiveTimeoutMs <= 0 || string.IsNullOrWhiteSpace(callbackId))
            return null;

        var timeoutMs = Math.Clamp(effectiveTimeoutMs, 100, 600_000);
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
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            lease,
            "workflow_loop timeout cleanup",
            ct);
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
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            pending.Lease,
            "workflow_loop retry backoff cleanup",
            ct);
    }

    private async Task EnsureCompensationPhaseDeadlineAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.CompensationPhaseDeadlineLease != null &&
            !string.IsNullOrWhiteSpace(state.CompensationPhaseDeadlineCallbackId))
        {
            return;
        }

        await ScheduleCompensationPhaseDeadlineAsync(state, ctx, ct);
    }

    private async Task ScheduleCompensationPhaseDeadlineAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await CancelCompensationPhaseDeadlineAsync(state, ctx, CancellationToken.None);

        var callbackId = BuildCompensationPhaseDeadlineCallbackId(state.RunId, ResolveInboundEnvelopeId(ctx));
        state.CompensationPhaseDeadlineCallbackId = callbackId;
        state.CompensationPhaseDeadlineLease = null;
        await SaveStateAsync(state, ctx, ct);

        RuntimeCallbackLease? lease = null;
        try
        {
            lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromMilliseconds(CompensationPhaseDeadlineMs),
                new WorkflowCompensationPhaseDeadlineFiredEvent
                {
                    RunId = state.RunId,
                },
                ct: ct);

            state.CompensationPhaseDeadlineLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            await SaveStateAsync(state, ctx, ct);
        }
        catch
        {
            state.CompensationPhaseDeadlineCallbackId = string.Empty;
            state.CompensationPhaseDeadlineLease = null;
            await SaveStateAsync(state, ctx, CancellationToken.None);
            if (lease != null)
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    lease,
                    "workflow_loop compensation phase deadline cleanup",
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task CancelCompensationPhaseDeadlineAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var lease = state.CompensationPhaseDeadlineLease?.Clone();
        state.CompensationPhaseDeadlineLease = null;
        state.CompensationPhaseDeadlineCallbackId = string.Empty;
        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            lease,
            "workflow_loop compensation phase deadline cleanup",
            ct);
    }

    private async Task HandleCompensationPhaseDeadlineFiredAsync(
        WorkflowCompensationPhaseDeadlineFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(evt.RunId);
        if (string.IsNullOrWhiteSpace(runId))
            return;

        var state = LoadState(ctx);
        if (!IsActiveRun(state, runId))
            return;

        if (!MatchesCompensationPhaseDeadline(state, envelope))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore compensation phase deadline without matching lease metadata run={RunId}",
                runId);
            return;
        }

        var error = $"compensation phase deadline exceeded after {CompensationPhaseDeadlineMs}ms";
        var result = await _stateHost.RecordCompensationPhaseDeadlineExceededAsync(runId, error, ct);
        await HandleCompensationTransitionAsync(
            result,
            runId,
            state.CurrentStepId,
            error,
            ctx,
            ct);
    }

    private async Task CleanupRunAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        bool preserveTerminalFacts = false,
        bool preserveCurrentStepInputVariable = false)
    {
        var timeoutLeases = state.TimeoutsByStepId.Values.ToList();
        var retryLeases = state.RetryBackoffsByStepId.Values.Select(x => x.Lease).ToList();
        var compensationPhaseDeadlineLease = state.CompensationPhaseDeadlineLease?.Clone();
        var terminalStepId = preserveTerminalFacts
            ? state.CurrentStepId
            : string.Empty;
        var terminalStepInput = preserveTerminalFacts
            ? state.CurrentStepInput
            : string.Empty;
        var terminalStepInputFileRefs = preserveTerminalFacts
            ? state.CurrentStepInputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray()
            : [];
        var terminalVariables = preserveTerminalFacts
            ? state.Variables.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
            : [];
        var terminalUsage = preserveTerminalFacts
            ? state.Usage?.Clone() ?? new WorkflowUsageMetricsState()
            : new WorkflowUsageMetricsState();
        var terminalIdempotency = preserveTerminalFacts
            ? state.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => x.Value.Clone(),
                StringComparer.Ordinal)
            : [];
        var terminalCompensationExecutionIds = preserveTerminalFacts
            ? state.CompensationExecutionIdsByStepId.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal)
            : [];

        state.Active = false;
        state.RunId = string.Empty;
        state.CurrentStepId = terminalStepId;
        state.CurrentStepInput = terminalStepInput;
        state.CurrentStepInputFileRefs.Clear();
        state.CurrentStepInputFileRefs.Add(terminalStepInputFileRefs.Select(static fileRef => fileRef.Clone()));
        state.InputFileRefs.Clear();
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.CompensationPhaseDeadlineCallbackId = string.Empty;
        state.CompensationPhaseDeadlineLease = null;
        state.Variables.Clear();
        foreach (var (key, value) in terminalVariables)
            state.Variables[key] = value ?? string.Empty;
        if (preserveCurrentStepInputVariable && !string.IsNullOrWhiteSpace(terminalStepInput))
            state.Variables["input"] = terminalStepInput;
        state.RetryAttemptsByStepId.Clear();
        state.TimeoutsByStepId.Clear();
        state.RetryBackoffsByStepId.Clear();
        state.ExecutionIdsByStepId.Clear();
        state.IdempotencyByStepId.Clear();
        state.CompensationExecutionIdsByStepId.Clear();
        foreach (var (key, value) in terminalIdempotency)
            state.IdempotencyByStepId[key] = value;
        foreach (var (key, value) in terminalCompensationExecutionIds)
            state.CompensationExecutionIdsByStepId[key] = value ?? string.Empty;
        state.Usage = terminalUsage;
        await SaveStateAsync(state, ctx, ct);

        foreach (var lease in timeoutLeases)
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                lease,
                "workflow_loop run timeout cleanup",
                ct);
        foreach (var lease in retryLeases)
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                lease,
                "workflow_loop run retry cleanup",
                ct);
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            compensationPhaseDeadlineLease,
            "workflow_loop run compensation phase deadline cleanup",
            ct);
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
            state.RetryBackoffsByStepId.Count == 0 &&
            string.IsNullOrWhiteSpace(state.CompensationPhaseDeadlineCallbackId) &&
            state.CompensationPhaseDeadlineLease == null &&
            state.ExecutionIdsByStepId.Count == 0 &&
            state.IdempotencyByStepId.Count == 0 &&
            state.CompensationExecutionIdsByStepId.Count == 0 &&
            state.InputFileRefs.Count == 0 &&
            state.CurrentStepInputFileRefs.Count == 0 &&
            IsEmptyUsage(state.Usage))
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

    private static bool IsDuplicateWorkflowCallStart(
        WorkflowExecutionKernelState state,
        StartWorkflowEvent evt,
        string runId)
    {
        if (!IsActiveRun(state, runId))
            return false;

        if (!evt.Parameters.TryGetValue(WorkflowCallInvocationIdParameterKey, out var requestedInvocationId) ||
            string.IsNullOrWhiteSpace(requestedInvocationId))
        {
            return false;
        }

        return state.Variables.TryGetValue(WorkflowCallInvocationIdParameterKey, out var activeInvocationId) &&
               string.Equals(activeInvocationId, requestedInvocationId.Trim(), StringComparison.Ordinal);
    }

    private static void ApplyStepUsage(
        StepCompletedEvent evt,
        WorkflowExecutionKernelState state)
    {
        if (!HasUsage(evt.Usage))
        {
            MirrorRunUsageVariables(state);
            return;
        }

        state.Usage ??= new WorkflowUsageMetricsState();
        state.Usage.PromptTokens += Math.Max(0, evt.Usage.PromptTokens);
        state.Usage.CompletionTokens += Math.Max(0, evt.Usage.CompletionTokens);
        state.Usage.TotalTokens += Math.Max(0, evt.Usage.TotalTokens);
        if (!string.IsNullOrWhiteSpace(evt.Usage.Model))
            state.Usage.Model = evt.Usage.Model.Trim();
        if (evt.Usage.Cost > 0)
            state.Usage.Cost += evt.Usage.Cost;
        if (evt.Usage.LatencyMs > 0)
            state.Usage.LatencyMs += evt.Usage.LatencyMs;

        MirrorRunUsageVariables(state);
        MirrorStepUsageVariables(state, evt.StepId, evt.Usage);
    }

    private static void MirrorRunUsageVariables(WorkflowExecutionKernelState state)
    {
        state.Usage ??= new WorkflowUsageMetricsState();
        state.Variables["workflow.usage.prompt_tokens"] = state.Usage.PromptTokens.ToString(CultureInfo.InvariantCulture);
        state.Variables["workflow.usage.completion_tokens"] = state.Usage.CompletionTokens.ToString(CultureInfo.InvariantCulture);
        state.Variables["workflow.usage.total_tokens"] = state.Usage.TotalTokens.ToString(CultureInfo.InvariantCulture);
        state.Variables["workflow.usage.model"] = state.Usage.Model ?? string.Empty;
        state.Variables["workflow.usage.cost"] = state.Usage.Cost.ToString("G17", CultureInfo.InvariantCulture);
        state.Variables["workflow.usage.latency_ms"] = state.Usage.LatencyMs.ToString(CultureInfo.InvariantCulture);
    }

    private static void MirrorStepCompletionVariables(
        WorkflowExecutionKernelState state,
        StepCompletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.StepId))
            return;

        var prefix = $"steps.{evt.StepId}";
        state.Variables[$"{prefix}.output"] = evt.Output ?? string.Empty;
        state.Variables[$"{prefix}.success"] = evt.Success ? "true" : "false";
        state.Variables[$"{prefix}.error"] = evt.Error ?? string.Empty;
        state.Variables[$"{prefix}.branch_key"] = evt.BranchKey ?? string.Empty;
        state.Variables[$"{prefix}.next_step_id"] = evt.NextStepId ?? string.Empty;
        state.Variables[$"{prefix}.assigned_variable"] = evt.AssignedVariable ?? string.Empty;
        state.Variables[$"{prefix}.assigned_value"] = evt.AssignedValue ?? string.Empty;

        foreach (var (key, value) in evt.Annotations)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            state.Variables[$"{prefix}.annotations.{key.Trim()}"] = value ?? string.Empty;
        }

        if (evt.Success && !string.IsNullOrWhiteSpace(evt.Output))
            MirrorJsonObjectVariables(state, prefix, evt.Output);

        if (HasUsage(evt.Usage))
            MirrorStepUsageVariables(state, evt.StepId, evt.Usage);
    }

    private static void MirrorJsonObjectVariables(
        WorkflowExecutionKernelState state,
        string prefix,
        string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    continue;

                state.Variables[$"{prefix}.json.{property.Name.Trim()}"] = ToVariableValue(property.Value);
            }
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static string ToVariableValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };

    private static void MirrorStepUsageVariables(
        WorkflowExecutionKernelState state,
        string stepId,
        WorkflowUsageMetrics usage)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            return;

        var prefix = $"steps.{stepId}.usage";
        state.Variables[$"{prefix}.prompt_tokens"] = Math.Max(0, usage.PromptTokens).ToString(CultureInfo.InvariantCulture);
        state.Variables[$"{prefix}.completion_tokens"] = Math.Max(0, usage.CompletionTokens).ToString(CultureInfo.InvariantCulture);
        state.Variables[$"{prefix}.total_tokens"] = Math.Max(0, usage.TotalTokens).ToString(CultureInfo.InvariantCulture);
        state.Variables[$"{prefix}.model"] = usage.Model ?? string.Empty;
        state.Variables[$"{prefix}.cost"] = Math.Max(0, usage.Cost).ToString("G17", CultureInfo.InvariantCulture);
        state.Variables[$"{prefix}.latency_ms"] = Math.Max(0, usage.LatencyMs).ToString(CultureInfo.InvariantCulture);
    }

    private static bool HasUsage(WorkflowUsageMetrics? usage) =>
        usage != null &&
        (usage.PromptTokens > 0 ||
         usage.CompletionTokens > 0 ||
         usage.TotalTokens > 0 ||
         !string.IsNullOrWhiteSpace(usage.Model) ||
         usage.Cost > 0 ||
         usage.LatencyMs > 0);

    private static bool IsEmptyUsage(WorkflowUsageMetricsState? usage) =>
        usage == null ||
        (usage.PromptTokens == 0 &&
         usage.CompletionTokens == 0 &&
         usage.TotalTokens == 0 &&
         string.IsNullOrWhiteSpace(usage.Model) &&
         Math.Abs(usage.Cost) < double.Epsilon &&
         usage.LatencyMs == 0);

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

    private static void ApplyForkSeedIdempotency(
        WorkflowExecutionKernelState state,
        WorkflowRunForkSeed? forkSeed)
    {
        if (forkSeed?.StartStepIdempotency == null ||
            string.IsNullOrWhiteSpace(forkSeed.StartAtStepId))
        {
            return;
        }

        var idempotency = WorkflowSideEffectIdempotencyKeyResolver.NormalizeIdentity(forkSeed.StartStepIdempotency);
        if (string.IsNullOrWhiteSpace(idempotency.StepId))
            idempotency.StepId = forkSeed.StartAtStepId.Trim();
        if (string.IsNullOrWhiteSpace(idempotency.LogicalRunId))
            idempotency.LogicalRunId = NormalizeRunId(forkSeed.SourceRunId);
        if (string.IsNullOrWhiteSpace(idempotency.IdempotencyKey))
            idempotency.IdempotencyKey = WorkflowSideEffectIdempotencyKeyResolver.BuildDefaultKey(idempotency);

        state.IdempotencyByStepId[idempotency.StepId] = idempotency;
    }

    private StepRequestEvent BuildStepRequest(
        StepDefinition step,
        string input,
        IEnumerable<WorkflowFileRef> inputFileRefs,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx)
    {
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        var effectiveTargetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(_workflow, step);
        // Do NOT log step input content: tool-call arguments routinely carry secrets
        // (e.g. {"token":"<NyxID JWT>"}). A preview leaked partial credentials into
        // stdout -> Elasticsearch. Length only.
        ctx.Logger.LogInformation(
            "workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars)",
            step.Id,
            canonicalStepType,
            string.IsNullOrWhiteSpace(effectiveTargetRole) ? "(none)" : effectiveTargetRole,
            input.Length);

        var request = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = canonicalStepType,
            RunId = state.RunId,
            Input = input,
            TargetRole = effectiveTargetRole,
        };
        request.InputFileRefs.Add(inputFileRefs.Select(static fileRef => fileRef.Clone()));

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
            ApplyAgentToolScope(request, role?.AgentToolScope, step.AgentToolScope);
        }
        else
        {
            ApplyAgentToolScope(request, roleScope: null, step.AgentToolScope);
        }

        ApplyExternalInvocation(request, step);
        ApplyTransformOperation(request, step.TransformOperation, state);
        ApplyHumanApprovalOptions(request, step.HumanApprovalOptions);
        ApplyExternalApprovalOptions(request, step.ExternalApprovalOptions, state);
        ApplyConnectorApprovalOptions(request, step.ConnectorApprovalOptions, state);
        ApplyInteractionPresentation(request, step.Presentation, state);

        return request;
    }

    // The call-site identity a step carries at runtime must be the one admission committed, so both
    // sides derive it from the compiler. Looping primitives receive their synthesized sub-step
    // call site here and copy it onto every item/iteration they dispatch.
    private void ApplyExternalInvocation(StepRequestEvent request, StepDefinition step)
    {
        var workflowName = _workflow?.Name ?? string.Empty;
        try
        {
            var invocation =
                WorkflowAuthorizationDependencyEvaluator.TryCompileDirectInvocation(workflowName, step)
                ?? WorkflowAuthorizationDependencyEvaluator.TryCompileSynthesizedSubStepInvocation(workflowName, step);
            if (invocation is not null)
                request.ExternalInvocation = invocation;
        }
        catch (WorkflowExternalCapabilityValidationException)
        {
            // A step that cannot be compiled into a call site stays unadmitted. Tools that require
            // admission fail closed before dispatch instead of aborting the whole execution turn.
        }
    }

    private void ApplyTransformOperation(
        StepRequestEvent request,
        TransformOperationSpec? transformOperation,
        WorkflowExecutionKernelState state)
    {
        if (transformOperation is null ||
            transformOperation.Kind == TransformOperationKind.Unspecified)
        {
            return;
        }

        var spec = transformOperation.Clone();
        spec.Key = _expressionEvaluator.Evaluate(spec.Key, state.Variables);
        spec.Value = _expressionEvaluator.Evaluate(spec.Value, state.Variables);
        (request.StepParameters ??= new WorkflowStepParameters()).TransformOperation = spec;
    }

    private static void ApplyHumanApprovalOptions(
        StepRequestEvent request,
        HumanApprovalOptionsDefinition? options)
    {
        if (options == null)
            return;

        var decision = ParseHumanApprovalTimeoutDefaultDecision(options.TimeoutDefaultDecision);
        if (decision == WorkflowHumanApprovalTimeoutDefaultDecision.Unspecified)
            return;

        (request.StepParameters ??= new WorkflowStepParameters()).HumanApproval = new WorkflowHumanApprovalOptions
        {
            TimeoutDefaultDecision = decision,
        };
    }

    private static WorkflowHumanApprovalTimeoutDefaultDecision ParseHumanApprovalTimeoutDefaultDecision(string? value) =>
        NormalizeOptionToken(value) switch
        {
            "approve" => WorkflowHumanApprovalTimeoutDefaultDecision.Approve,
            "approved" => WorkflowHumanApprovalTimeoutDefaultDecision.Approve,
            "reject" => WorkflowHumanApprovalTimeoutDefaultDecision.Reject,
            "rejected" => WorkflowHumanApprovalTimeoutDefaultDecision.Reject,
            _ => WorkflowHumanApprovalTimeoutDefaultDecision.Unspecified,
        };

    private void ApplyExternalApprovalOptions(
        StepRequestEvent request,
        ExternalApprovalWaitOptionsDefinition? options,
        WorkflowExecutionKernelState state)
    {
        if (options == null)
            return;

        var sourceId = EvaluateOption(options.SourceId, state);
        var externalIdKind = EvaluateOption(options.ExternalIdKind, state);
        var externalId = EvaluateOption(options.ExternalId, state);
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(externalIdKind) ||
            string.IsNullOrWhiteSpace(externalId))
        {
            return;
        }

        (request.StepParameters ??= new WorkflowStepParameters()).ExternalApproval =
            new WorkflowExternalApprovalWaitOptions
            {
                SourceId = sourceId,
                ExternalIdKind = externalIdKind,
                ExternalId = externalId,
                SignalName = EvaluateOption(options.SignalName, state),
                CallbackIdempotencyKey = EvaluateOption(options.CallbackIdempotencyKey, state),
                RequestId = EvaluateOption(options.RequestId, state),
            };
    }

    private string EvaluateOption(string? value, WorkflowExecutionKernelState state) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : _expressionEvaluator.Evaluate(value, state.Variables).Trim();

    private void ApplyConnectorApprovalOptions(
        StepRequestEvent request,
        ConnectorApprovalOptionsDefinition? options,
        WorkflowExecutionKernelState state)
    {
        if (options == null)
            return;

        (request.StepParameters ??= new WorkflowStepParameters()).ConnectorApproval =
            new WorkflowConnectorApprovalOptions
            {
                Policy = WorkflowExternalActionApprovalPolicy.Required,
                ServiceRef = EvaluateOption(options.ServiceRef, state),
                NodeId = EvaluateOption(options.NodeId, state),
                HttpVerb = EvaluateOption(options.HttpVerb, state),
                Resource = EvaluateOption(options.Resource, state),
                PermissionScope = EvaluateOption(options.PermissionScope, state),
                ExpirationSeconds = options.ExpirationSeconds,
                StatusCheckIntervalSeconds = options.StatusCheckIntervalSeconds,
                Destructive = options.Destructive,
                TeamId = EvaluateOption(options.TeamId, state),
                MemberId = EvaluateOption(options.MemberId, state),
                WorkflowId = EvaluateOption(options.WorkflowId, state),
                PublishedServiceId = EvaluateOption(options.PublishedServiceId, state),
                PolicyReason = string.IsNullOrWhiteSpace(options.PolicyReason)
                    ? "workflow-step-required-approval"
                    : EvaluateOption(options.PolicyReason, state),
            };
    }

    private static string NormalizeOptionToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static void ApplyAgentToolScope(
        StepRequestEvent request,
        WorkflowAgentToolScopeDefinition? roleScope,
        WorkflowAgentToolScopeDefinition? stepScope)
    {
        var effectiveScope = IntersectAgentToolScope(roleScope, stepScope);
        if (effectiveScope == null)
            return;

        var payload = (request.StepParameters ??= new WorkflowStepParameters()).AgentToolScope = new WorkflowAgentToolScope
        {
            RestrictAllowedToolNames = effectiveScope.RestrictAllowedToolNames,
            RestrictToolSets = effectiveScope.RestrictToolSets,
        };
        foreach (var toolName in effectiveScope.AllowedToolNames)
            payload.AllowedToolNames.Add(toolName);
        foreach (var toolSetRef in effectiveScope.ToolSetRefs)
            payload.ToolSetRefs.Add(toolSetRef);
    }

    private static WorkflowAgentToolScopeDefinition? IntersectAgentToolScope(
        WorkflowAgentToolScopeDefinition? roleScope,
        WorkflowAgentToolScopeDefinition? stepScope)
    {
        if (roleScope == null)
            return stepScope == null ? null : CloneAgentToolScope(stepScope);

        if (stepScope == null)
            return CloneAgentToolScope(roleScope);

        var roleRestrictsAllowed = RestrictsAllowedToolNames(roleScope);
        var stepRestrictsAllowed = RestrictsAllowedToolNames(stepScope);
        var roleRestrictsToolSets = RestrictsToolSets(roleScope);
        var stepRestrictsToolSets = RestrictsToolSets(stepScope);
        return new WorkflowAgentToolScopeDefinition
        {
            RestrictAllowedToolNames = roleRestrictsAllowed || stepRestrictsAllowed,
            RestrictToolSets = roleRestrictsToolSets || stepRestrictsToolSets,
            AllowedToolNames = IntersectScopeDimension(
                roleScope.AllowedToolNames,
                roleRestrictsAllowed,
                stepScope.AllowedToolNames,
                stepRestrictsAllowed),
            ToolSetRefs = IntersectScopeDimension(
                roleScope.ToolSetRefs,
                roleRestrictsToolSets,
                stepScope.ToolSetRefs,
                stepRestrictsToolSets),
        };
    }

    private static List<string> IntersectScopeDimension(
        IEnumerable<string> roleValues,
        bool roleRestricts,
        IEnumerable<string> stepValues,
        bool stepRestricts)
    {
        if (!roleRestricts)
            return stepValues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!stepRestricts)
            return roleValues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var stepSet = new HashSet<string>(stepValues, StringComparer.OrdinalIgnoreCase);
        return roleValues.Where(stepSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool RestrictsAllowedToolNames(WorkflowAgentToolScopeDefinition scope) =>
        scope.RestrictAllowedToolNames || scope.AllowedToolNames.Count > 0;

    private static bool RestrictsToolSets(WorkflowAgentToolScopeDefinition scope) =>
        scope.RestrictToolSets || scope.ToolSetRefs.Count > 0;

    private static WorkflowAgentToolScopeDefinition CloneAgentToolScope(WorkflowAgentToolScopeDefinition scope) =>
        new()
        {
            RestrictAllowedToolNames = RestrictsAllowedToolNames(scope),
            RestrictToolSets = RestrictsToolSets(scope),
            AllowedToolNames = scope.AllowedToolNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ToolSetRefs = scope.ToolSetRefs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

    private void ApplyInteractionPresentation(
        StepRequestEvent request,
        StepPresentation? presentation,
        WorkflowExecutionKernelState state)
    {
        ApplyDeliveryTargetId(request, presentation, state);
        ApplyInteractionSpec(request, presentation, state);
        ApplyInteractionTemplateSpec(request, presentation, state);
    }

    private void ApplyDeliveryTargetId(
        StepRequestEvent request,
        StepPresentation? presentation,
        WorkflowExecutionKernelState state)
    {
        if (string.IsNullOrWhiteSpace(presentation?.DeliveryTargetId))
            return;

        (request.StepParameters ??= new WorkflowStepParameters()).DeliveryTargetId =
            _expressionEvaluator.Evaluate(presentation.DeliveryTargetId.Trim(), state.Variables);
    }

    private void ApplyInteractionSpec(
        StepRequestEvent request,
        StepPresentation? presentation,
        WorkflowExecutionKernelState state)
    {
        if (!StepPresentation.HasInteractionSpec(presentation?.InteractionSpec))
            return;

        var spec = presentation!.InteractionSpec!.Clone();
        spec.Title = _expressionEvaluator.Evaluate(spec.Title, state.Variables);
        spec.Body = _expressionEvaluator.Evaluate(spec.Body, state.Variables);
        EvaluateActions(spec.Actions, state);
        EvaluateFields(spec.Fields, state);
        EvaluateCards(spec.Cards, state);
        (request.StepParameters ??= new WorkflowStepParameters()).InteractionSpec = spec;
    }

    private void ApplyInteractionTemplateSpec(
        StepRequestEvent request,
        StepPresentation? presentation,
        WorkflowExecutionKernelState state)
    {
        if (!StepPresentation.HasInteractionTemplateSpec(presentation?.InteractionTemplateSpec))
            return;

        var spec = presentation!.InteractionTemplateSpec!.Clone();
        spec.TemplateId = _expressionEvaluator.Evaluate(spec.TemplateId, state.Variables);
        var evaluatedVariables = spec.TemplateVariable
            .Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                _expressionEvaluator.Evaluate(pair.Value, state.Variables)))
            .ToArray();
        spec.TemplateVariable.Clear();
        foreach (var (key, value) in evaluatedVariables)
            spec.TemplateVariable[key] = value;

        (request.StepParameters ??= new WorkflowStepParameters()).InteractionTemplateSpec = spec;
    }

    private void EvaluateActions(
        IEnumerable<Aevatar.Foundation.Abstractions.Interactions.InteractionAction> actions,
        WorkflowExecutionKernelState state)
    {
        foreach (var action in actions)
        {
            action.Label = _expressionEvaluator.Evaluate(action.Label, state.Variables);
            action.Value = _expressionEvaluator.Evaluate(action.Value, state.Variables);
            action.Placeholder = _expressionEvaluator.Evaluate(action.Placeholder, state.Variables);
            foreach (var option in action.Options)
            {
                option.Label = _expressionEvaluator.Evaluate(option.Label, state.Variables);
                option.Value = _expressionEvaluator.Evaluate(option.Value, state.Variables);
            }
        }
    }

    private void EvaluateFields(
        IEnumerable<Aevatar.Foundation.Abstractions.Interactions.InteractionField> fields,
        WorkflowExecutionKernelState state)
    {
        foreach (var field in fields)
        {
            field.Title = _expressionEvaluator.Evaluate(field.Title, state.Variables);
            field.Text = _expressionEvaluator.Evaluate(field.Text, state.Variables);
        }
    }

    private void EvaluateCards(
        IEnumerable<Aevatar.Foundation.Abstractions.Interactions.InteractionCard> cards,
        WorkflowExecutionKernelState state)
    {
        foreach (var card in cards)
        {
            card.Title = _expressionEvaluator.Evaluate(card.Title, state.Variables);
            card.Text = _expressionEvaluator.Evaluate(card.Text, state.Variables);
            card.ImageUrl = _expressionEvaluator.Evaluate(card.ImageUrl, state.Variables);
            EvaluateFields(card.Fields, state);
            EvaluateActions(card.Actions, state);
        }
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

        var request = BuildStepRequest(step, state.CurrentStepInput, state.CurrentStepInputFileRefs, state, ctx);

        // Restore the saved execution_id so stale-completion protection works after resume
        if (state.ExecutionIdsByStepId.TryGetValue(step.Id, out var savedExecutionId))
            request.ExecutionId = savedExecutionId;
        request.IdempotencyKey = ResolveAndPersistStepIdempotency(step, state).IdempotencyKey;

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
                    ResolveStepTimeoutMs(step, ResolveRetryDispatchKind(state, step.Id)),
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
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    timeoutLease,
                    "workflow_loop resumed timeout cleanup",
                    CancellationToken.None);
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

    private static bool MatchesCompensationPhaseDeadline(
        WorkflowExecutionKernelState state,
        EventEnvelope envelope)
    {
        if (state.CompensationPhaseDeadlineLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, state.CompensationPhaseDeadlineLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, state.CompensationPhaseDeadlineCallbackId, StringComparison.Ordinal);
    }

    private static string ResolveInboundEnvelopeId(IWorkflowExecutionContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.InboundEnvelope?.Id)
            ? Guid.NewGuid().ToString("N")
            : ctx.InboundEnvelope.Id;

    private static string BuildStepTimeoutCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-timeout", runId, stepId, originEnvelopeId);

    private static string BuildStepRetryBackoffCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-retry-backoff", runId, stepId, originEnvelopeId);

    private static string BuildCompensationPhaseDeadlineCallbackId(string runId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-compensation-phase-deadline", runId, originEnvelopeId);

    private static int ResolveStepTimeoutMs(StepDefinition step, WorkflowStepDispatchKind dispatchKind) =>
        dispatchKind == WorkflowStepDispatchKind.Compensation
            ? step.TimeoutMs is > 0 ? step.TimeoutMs.Value : DefaultCompensationTimeoutMs
            : step.TimeoutMs is > 0 ? step.TimeoutMs.Value : 0;

    private static WorkflowStepDispatchKind ResolveRetryDispatchKind(
        WorkflowExecutionKernelState state,
        string stepId) =>
        state.CompensationExecutionIdsByStepId.ContainsKey(stepId)
            ? WorkflowStepDispatchKind.Compensation
            : WorkflowStepDispatchKind.Forward;

}
