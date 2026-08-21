using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Modules;
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

    internal const string ModuleStateKey = "workflow_execution_kernel";
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
                payload.Is(WorkflowExecutionRecoveryRequestedEvent.Descriptor) ||
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
        var kernelState = LoadState(workflowContext);
        if (kernelState.PendingWorkflowCompletion != null)
        {
            await PublishPreparedWorkflowCompletionAsync(kernelState, workflowContext, ct);
            return;
        }

        var payload = envelope.Payload;
        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            await HandleStartWorkflowAsync(payload.Unpack<StartWorkflowEvent>(), workflowContext, ct);
            return;
        }

        if (payload.Is(WorkflowExecutionRecoveryRequestedEvent.Descriptor))
        {
            await HandleExecutionRecoveryRequestedAsync(
                payload.Unpack<WorkflowExecutionRecoveryRequestedEvent>(),
                workflowContext,
                ct);
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
            await HandleStepCompletedAsync(payload.Unpack<StepCompletedEvent>(), envelope, workflowContext, ct);
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

            // Top-level starts do not carry a workflow-call invocation id. The run actor and
            // envelope identity already provide the idempotency boundary, so an at-least-once
            // redelivery for the same run must not turn that run into a terminal failure.
            if (IsActiveRun(state, runId) && !HasWorkflowCallInvocationId(evt))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore duplicate top-level start run={RunId}",
                    runId);
                return;
            }

            await PrepareWorkflowCompletionAsync(
                state,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = "workflow run is already active",
                },
                ctx,
                ct);
            await PublishPreparedWorkflowCompletionAsync(state, ctx, ct);
            return;
        }

        var forkSeed = evt.ForkSeed;
        var normalizedWritesGranted = WorkflowNormalizedStateWriteAdmission.IsGranted(
            _stateHost.RuntimeStateSchemaContextReader);
        var representation = evt.ValueRepresentation;
        if (!Enum.IsDefined(representation))
            throw new InvalidOperationException("Workflow start declares an unknown value representation.");
        if (representation == WorkflowExecutionValueRepresentation.Unspecified)
        {
            if (forkSeed?.NormalizedValues != null)
                throw new InvalidOperationException(
                    "A normalized workflow fork must declare the normalized value representation.");
            // Serialized pre-admission starts did not carry this field. They
            // remain readable as legacy starts, but never opt into normalized
            // writes by inference.
            representation = WorkflowExecutionValueRepresentation.Legacy;
        }
        if (representation == WorkflowExecutionValueRepresentation.Normalized)
        {
            if (!normalizedWritesGranted)
                throw new InvalidOperationException(
                    "A normalized workflow start requires a runtime-owned schema adoption receipt.");
            if (forkSeed != null && forkSeed.NormalizedValues == null)
                throw new InvalidOperationException(
                    "A legacy workflow fork seed cannot start with the normalized value representation.");
        }
        else if (forkSeed?.NormalizedValues != null)
        {
            throw new InvalidOperationException(
                "A normalized workflow fork seed cannot be downgraded to the legacy value representation.");
        }
        if (WorkflowValueLifecyclePolicy.HasDeclarations(_workflow) &&
            (representation != WorkflowExecutionValueRepresentation.Normalized ||
             !WorkflowNormalizedStateWriteAdmission.IsValueLifecycleGranted(
                 _stateHost.RuntimeStateSchemaContextReader)))
        {
            throw WorkflowValueLifecycleException.SchemaUnavailable();
        }

        state.Active = true;
        state.RunId = runId;
        var hasForkSeedStart = forkSeed != null && !string.IsNullOrWhiteSpace(forkSeed.StartAtStepId);
        state.CurrentStepId = string.Empty;
        state.CurrentStepInput = string.Empty;
        state.CurrentStepInputFileRefs.Clear();
        state.InputFileRefs.Clear();
        state.Variables.Clear();
        if (forkSeed?.NormalizedValues != null && forkSeed.Variables.Count > 0)
        {
            throw new InvalidOperationException(
                "A normalized workflow fork seed cannot also carry expanded legacy variables.");
        }
        if (representation == WorkflowExecutionValueRepresentation.Normalized &&
            forkSeed?.NormalizedValues != null)
        {
            WorkflowNormalizedExecutionSeedCodec.Restore(state, forkSeed.NormalizedValues);
        }
        else if (representation == WorkflowExecutionValueRepresentation.Normalized && forkSeed == null)
        {
            WorkflowExecutionValueStore.Initialize(state);
        }
        else
        {
            // A legacy fork seed is an explicit representation boundary. A
            // v1-adopted target may execute it, but must not partially infer a
            // normalized completion ledger from flat aliases.
            state.NormalizedValues = null;
        }
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
        state.CompensationTerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        state.PendingCompensationOutcome = null;
        if (evt.WorkflowRuntime != null)
        {
            await _stateHost.UpdateExecutionContextAsync(
                WorkflowRunExecutionContextStateAccess.BuildWorkflowRuntimeDelta(evt.WorkflowRuntime),
                ct);
        }

        if (hasForkSeedStart)
        {
            if (WorkflowExecutionValueStore.IsNormalized(state) &&
                forkSeed!.NormalizedValues != null)
            {
                WorkflowNormalizedExecutionSeedCodec.ApplyOverrides(
                    state,
                    forkSeed.VariableOverrides);
                if (!WorkflowExecutionValueStore.CreateVariableView(state).ContainsKey("input"))
                {
                    _ = WorkflowExecutionValueStore.CaptureInputValue(
                        state,
                        evt.Input ?? string.Empty,
                        WorkflowCanonicalValueSourceKind.InitialInput);
                }
            }
            else
            {
                MergeStartParametersIntoVariables(state.Variables, forkSeed!.Variables);
            }
        }
        else
        {
            state.Variables["input"] = evt.Input ?? string.Empty;
        }
        if (!WorkflowExecutionValueStore.IsNormalized(state))
            state.Variables["input"] = evt.Input ?? string.Empty;
        ApplyForkSeedIdempotency(state, forkSeed);
        state.InputFileRefs.Add(evt.InputFileRefs.Select(static fileRef => fileRef.Clone()));
        MirrorRunUsageVariables(state);
        WorkflowExecutionValueStore.CaptureInitialVariables(state);
        if (WorkflowExecutionValueStore.IsNormalized(state))
            WorkflowNormalizedExecutionSeedCodec.ApplyOverrides(state, evt.Parameters);
        else
            MergeStartParametersIntoVariables(state.Variables, evt.Parameters);
        await SaveStateAsync(state, ctx, ct);

        var entry = hasForkSeedStart
            ? _workflow.GetStep(forkSeed!.StartAtStepId)
            : _workflow.Steps.FirstOrDefault();
        if (entry == null)
        {
            var error = hasForkSeedStart
                ? $"fork seed start step '{forkSeed!.StartAtStepId}' was not found"
                : "无步骤";
            await CompleteRunAndPublishAsync(
                state,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = error,
                },
                ctx,
                ct,
                preserveTerminalFacts: false);
            return;
        }

        var variableView = WorkflowExecutionValueStore.CreateVariableView(state);
        var startInput = WorkflowExecutionValueStore.IsNormalized(state)
            ? variableView.TryGetValue("input", out var resolvedInput)
                ? resolvedInput
                : evt.Input ?? string.Empty
            : hasForkSeedStart && forkSeed!.Variables.TryGetValue("input", out var legacySeedInput)
                ? legacySeedInput ?? string.Empty
                : evt.Input ?? string.Empty;
        var startInputValueId = WorkflowExecutionValueStore.GetBindingValueId(state, "input");
        await DispatchStepAsync(
            entry,
            startInput,
            state.InputFileRefs,
            state,
            WorkflowStepDispatchKind.Forward,
            ctx,
            ct,
            startInputValueId);
    }

    private async Task HandleExecutionRecoveryRequestedAsync(
        WorkflowExecutionRecoveryRequestedEvent request,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runId = NormalizeRunId(request.RunId);
        var state = LoadState(ctx);
        if (state.PendingCompensationOutcome?.Completion != null)
        {
            if (IsActiveRun(state, runId))
                await ResumePendingCompensationOutcomeAsync(state, ctx, ct);
            return;
        }

        if (!IsActiveRun(state, runId) || !state.CurrentStepDispatchPending)
            return;

        await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
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
            ExecutionId = state.ExecutionIdsByStepId.GetValueOrDefault(stepId, string.Empty),
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
            FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        }, TopologyAudience.Self, ct);

        state.TimeoutsByStepId.Remove(stepId);
        state.CurrentStepTimeoutCallbackId = string.Empty;
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleStepCompletedAsync(
        StepCompletedEvent evt,
        EventEnvelope envelope,
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

        if (WorkflowExecutionValueStore.IsNormalized(state) &&
            !string.Equals(envelope.Route?.PublisherActorId, ctx.AgentId, StringComparison.Ordinal))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: reject non-self normalized completion actor={ActorId} publisher={PublisherActorId} step={StepId}",
                ctx.AgentId,
                envelope.Route?.PublisherActorId ?? string.Empty,
                evt.StepId);
            return;
        }

        if (!MatchesCurrentStep(state, evt.StepId) && state.CurrentStepDispatchPending)
        {
            await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
            state = LoadState(ctx);
        }

        if (WorkflowExecutionValueStore.IsNormalized(state) &&
            WorkflowExecutionValueStore.TryGetInternalDispatch(state, evt, out var internalDispatch))
        {
            WorkflowExecutionValueStore.RecordInternalOutput(
                state,
                evt,
                internalDispatch.InputValueId,
                ResolveReplayEvidence());
            WorkflowExecutionValueStore.ConsumeInternalDispatch(state, internalDispatch);
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        var current = _workflow.GetStep(evt.StepId);
        if (current == null)
        {
            if (WorkflowExecutionValueStore.IsNormalized(state))
            {
                ctx.Logger.LogWarning(
                    "workflow_loop: reject internal completion without exact actor-owned dispatch run={RunId} step={StepId} execution={ExecutionId}",
                    runId,
                    evt.StepId,
                    evt.ExecutionId);
                return;
            }

            if (IsCurrentForEachDirectChildCompletion(state, evt, ctx))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore legacy foreach child completion step={StepId} parent={ParentStepId}",
                    evt.StepId,
                    state.CurrentStepId);
                return;
            }

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

        // An accepted normalized completion can be redelivered after the actor
        // commits the source-step result but before it publishes the successor.
        // Treat that delivery as recovery of the committed transition, never as
        // a second workflow advance.
        var normalizedAcceptedReplay = WorkflowExecutionValueStore.IsNormalized(state) &&
                                       WorkflowExecutionValueStore.IsAcceptedCompletion(state, evt);
        if (normalizedAcceptedReplay)
        {
            // IsAcceptedCompletion is a cheap ledger/source-identity gate.
            // Reuse the full exact-replay validator before recovery so changed
            // branch, outcome, usage, assignment, or annotation fields fail
            // closed instead of steering a committed transition.
            _ = WorkflowExecutionValueStore.RecordStepCompletion(state, evt, ResolveReplayEvidence());
            if (state.NormalizedValues!.CompletedSteps.TryGetValue(evt.StepId, out var committed) &&
                committed.Success)
            {
                if (state.CompensationExecutionIdsByStepId.TryGetValue(
                        evt.StepId,
                        out var compensationExecutionId) &&
                    !string.IsNullOrWhiteSpace(compensationExecutionId))
                {
                    _ = await TryRecordSuccessfulCompensationAsync(
                        evt,
                        compensationExecutionId,
                        state,
                        ctx,
                        ct);
                    return;
                }

                state.RetryAttemptsByStepId.Remove(evt.StepId);
                await CancelRetryBackoffAsync(state, evt.StepId, ctx, CancellationToken.None);
                await RecordCompensableStepOutcomeAsync(
                    current,
                    evt,
                    committed.OutputValueId,
                    state,
                    ct);
                await RecoverAcceptedCompletionTransitionAsync(state, current, evt, ctx, ct);
                return;
            }
        }

        try
        {
            // The runtime-owned dispatch ledger supplies omitted execution
            // identity before persistence; supplied mismatches remain stale.
            var hasExpectedExecutionId = state.ExecutionIdsByStepId.TryGetValue(
                evt.StepId,
                out var expectedExecutionId);
            if (hasExpectedExecutionId &&
                string.IsNullOrEmpty(evt.ExecutionId))
            {
                evt.ExecutionId = expectedExecutionId;
            }
            else if (!string.IsNullOrEmpty(evt.ExecutionId) &&
                     !string.IsNullOrEmpty(expectedExecutionId) &&
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

            if (WorkflowExecutionValueStore.IsNormalized(state) &&
                !normalizedAcceptedReplay &&
                !hasExpectedExecutionId)
            {
                ctx.Logger.LogWarning(
                    "workflow_loop: reject normalized completion without an active execution fence run={RunId} step={StepId} received={Received}",
                    runId,
                    evt.StepId,
                    evt.ExecutionId);
                await ctx.PublishAsync(new StaleStepCompletionRejectedEvent
                {
                    StepId = evt.StepId,
                    RunId = runId,
                    ExpectedExecutionId = string.Empty,
                    ReceivedExecutionId = evt.ExecutionId,
                }, TopologyAudience.Self, ct);
                return;
            }

            state.ExecutionIdsByStepId.Remove(evt.StepId);
            var compensationExecutionId = state.CompensationExecutionIdsByStepId.TryGetValue(evt.StepId, out var carriedCompensationExecutionId)
                ? carriedCompensationExecutionId
                : string.Empty;

            if (!evt.Success && state.RetryBackoffsByStepId.ContainsKey(evt.StepId))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore duplicate failed completion while retry backoff is pending run={RunId} step={StepId}",
                    runId,
                    evt.StepId);
                return;
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

            var completionOutputValueId = string.Empty;
            var normalizedCompletionReplay = normalizedAcceptedReplay;
            if (WorkflowExecutionValueStore.IsNormalized(state))
            {
                completionOutputValueId =
                    WorkflowExecutionValueStore.RecordStepCompletion(state, evt, ResolveReplayEvidence());
            }
            else
            {
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
            }

            if (!normalizedCompletionReplay)
                ApplyStepUsage(evt, state);

            // Persist failed-step timeout cleanup before retry/terminal
            // handling. Successful transitions stage their successor first so
            // completion acceptance and the next dispatch intent share the
            // same durable state boundary.
            if (!evt.Success)
                await CancelTimeoutAsync(state, evt.StepId, ctx, ct);

            if (evt.Success)
            {
                // RecordStepCompletion ran first, so a retry-backoff cleanup
                // commit cannot persist an execution-less state without the
                // normalized acceptance ledger.
                await CancelRetryBackoffAsync(state, evt.StepId, ctx, CancellationToken.None);
                await CancelTimeoutAsync(
                    state,
                    evt.StepId,
                    ctx,
                    CancellationToken.None,
                    persistState: false);
            }

            if (!WorkflowExecutionValueStore.IsNormalized(state))
                MirrorStepCompletionVariables(state, evt);

            if (WorkflowExecutionValueStore.IsNormalized(state) &&
                !evt.Success &&
                string.IsNullOrWhiteSpace(compensationExecutionId))
            {
                await RecordCompensableStepOutcomeAsync(
                    current,
                    evt,
                    completionOutputValueId,
                    state,
                    ct);
            }

            if (!evt.Success)
            {
                if (!string.IsNullOrWhiteSpace(compensationExecutionId))
                {
                    if (await TryRetryAsync(current, evt, state, ctx, ct))
                        return;

                    await TryRecordFailedCompensationDeadLetterAsync(evt, compensationExecutionId, state, ctx, ct);
                    return;
                }

                if (HasUncertainOutcome(evt) || IsTimeoutError(evt.Error))
                {
                    ctx.Logger.LogError(
                        "workflow_loop: run={RunId} step={StepId} has an uncertain external outcome and run will fail. error={Error}",
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
                            RecoveryFailureKind = evt.RecoveryFailureKind,
                        },
                        state,
                        evt,
                        ct);
                    return;
                }

                if (await TryRetryAsync(current, evt, state, ctx, ct))
                    return;
                if (await TryOnErrorAsync(
                        current,
                        evt,
                        completionOutputValueId,
                        state,
                        ctx,
                        ct))
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
                        RecoveryFailureKind = evt.RecoveryFailureKind,
                    },
                    state,
                    evt,
                    ct);
                return;
            }

            state.RetryAttemptsByStepId.Remove(evt.StepId);
            state.RetryBackoffsByStepId.Remove(evt.StepId);

            if (WorkflowExecutionValueStore.IsNormalized(state) &&
                string.IsNullOrWhiteSpace(compensationExecutionId))
            {
                await RecordCompensableStepOutcomeAsync(
                    current,
                    evt,
                    completionOutputValueId,
                    state,
                    ct);
            }

            WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
                state,
                current.ValueLifecycle,
                current.Id,
                evt.ExecutionId,
                _stateHost.IsValuePinnedForCompensation);

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
                await CompleteRunAndPublishAsync(
                    state,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = true,
                        Output = evt.Output,
                    },
                    ctx,
                    ct);
                return;
            }

            StageStepDispatch(
                next,
                evt.Output ?? string.Empty,
                state.InputFileRefs,
                state,
                WorkflowStepDispatchKind.Forward,
                ctx,
                completionOutputValueId);
            await SaveStateAsync(state, ctx, ct);
            await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
        }
        catch (Exception ex) when (
            !ct.IsCancellationRequested &&
            ex is not WorkflowDurablePublicationPendingException &&
            !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
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
                    RecoveryFailureKind = evt.RecoveryFailureKind,
                    ValueLifecycleFailureKind = ex is WorkflowValueLifecycleException lifecycleFailure
                        ? lifecycleFailure.Kind
                        : WorkflowValueLifecycleFailureKind.Unspecified,
                },
                state,
                evt,
                CancellationToken.None);
        }
    }

    private bool IsCurrentForEachDirectChildCompletion(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        IWorkflowExecutionContext ctx)
    {
        var childStepId = completion.StepId ?? string.Empty;
        var childExecutionId = completion.ExecutionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childStepId))
            return false;

        var forEachState = WorkflowExecutionStateAccess.Load<ForEachModuleState>(
            ctx,
            ForEachModule.ModuleStateKey);
        if (ForEachModule.IsCompletedChildAttempt(
                forEachState,
                completion.RunId,
                childStepId,
                childExecutionId))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(state.CurrentStepId))
            return false;

        var currentStep = _workflow.GetStep(state.CurrentStepId);
        if (currentStep == null ||
            !string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(currentStep.Type),
                "foreach",
                StringComparison.Ordinal) ||
            !ForEachModule.IsDirectChildStepId(currentStep.Id, childStepId) ||
            string.IsNullOrWhiteSpace(state.RunId) ||
            !state.ExecutionIdsByStepId.TryGetValue(currentStep.Id, out var parentExecutionId) ||
            string.IsNullOrWhiteSpace(parentExecutionId))
        {
            return false;
        }

        return forEachState.Parents.Values
            .Where(parent =>
                !string.IsNullOrWhiteSpace(parent.ParentRunId) &&
                !string.IsNullOrWhiteSpace(parent.ParentStepId) &&
                !string.IsNullOrWhiteSpace(parent.ParentExecutionId) &&
                string.Equals(parent.ParentRunId, state.RunId, StringComparison.Ordinal) &&
                string.Equals(parent.ParentStepId, currentStep.Id, StringComparison.Ordinal) &&
                string.Equals(parent.ParentExecutionId, parentExecutionId, StringComparison.Ordinal) &&
                parent.ChildExecutionIds.TryGetValue(childStepId, out var expectedChildExecutionId) &&
                !string.IsNullOrWhiteSpace(expectedChildExecutionId) &&
                (string.IsNullOrWhiteSpace(childExecutionId) ||
                 string.Equals(expectedChildExecutionId, childExecutionId, StringComparison.Ordinal)))
            .Take(2)
            .Count() == 1;
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

        string input;
        string? capturedInputValueId;
        if (WorkflowExecutionValueStore.IsNormalized(state))
        {
            capturedInputValueId = evt.CapturedOutputValueId?.Trim() ?? string.Empty;
            var canonical = WorkflowExecutionValueStore.GetCanonicalValue(state, capturedInputValueId);
            if (!string.Equals(canonical.ProducerStepId, evt.CapturedOutputProducerStepId, StringComparison.Ordinal) ||
                !string.Equals(canonical.ProducerExecutionId, evt.CapturedOutputProducerExecutionId, StringComparison.Ordinal) ||
                canonical.SourceKind != evt.CapturedOutputSourceKind)
            {
                throw new InvalidOperationException(
                    $"Normalized compensation for step '{compensationStepId}' has a mismatched canonical source identity.");
            }

            input = canonical.Value ?? string.Empty;
        }
        else
        {
            var usesCurrentStepInput = string.IsNullOrWhiteSpace(evt.CapturedOutput);
            input = usesCurrentStepInput
                ? WorkflowExecutionValueStore.ResolveCurrentStepInput(state)
                : evt.CapturedOutput;
            capturedInputValueId = null;
        }
        await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
        await DispatchStepAsync(
            step,
            input,
            state.CurrentStepInputFileRefs,
            state,
            WorkflowStepDispatchKind.Compensation,
            ctx,
            ct,
            capturedInputValueId);
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
        if (string.IsNullOrWhiteSpace(compensationExecutionId))
            return false;

        await EnsurePendingCompensationOutcomeAsync(
            state,
            evt,
            compensationExecutionId,
            ctx,
            ct);
        var result = await _stateHost.RecordCompensationStepCompletionAsync(new CompensationStepCompletedEvent
        {
            RunId = evt.RunId,
            CompensationStepId = evt.StepId,
            Success = true,
            ExecutionId = compensationExecutionId ?? string.Empty,
        }, ct);
        result = ResolveRecoveredCompensationTransition(result, success: true);
        await StagePendingCompensationContinuationAsync(state, result, evt, ctx, ct);
        return await DrainPendingCompensationContinuationAsync(state, ctx, ct);
    }

    private async Task<bool> TryRecordFailedCompensationDeadLetterAsync(
        StepCompletedEvent evt,
        string compensationExecutionId,
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(compensationExecutionId))
            return false;

        await EnsurePendingCompensationOutcomeAsync(
            state,
            evt,
            compensationExecutionId,
            ctx,
            ct);
        var result = await _stateHost.RecordCompensationStepCompletionAsync(new CompensationStepCompletedEvent
        {
            RunId = evt.RunId,
            CompensationStepId = evt.StepId,
            Success = false,
            Error = evt.Error ?? string.Empty,
            ExecutionId = compensationExecutionId ?? string.Empty,
        }, ct);
        result = ResolveRecoveredCompensationTransition(result, success: false);
        await StagePendingCompensationContinuationAsync(state, result, evt, ctx, ct);
        return await DrainPendingCompensationContinuationAsync(state, ctx, ct);
    }

    private async Task ResumePendingCompensationOutcomeAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var pending = state.PendingCompensationOutcome;
        var completion = pending?.Completion?.Clone();
        if (completion == null || string.IsNullOrWhiteSpace(pending!.CompensationExecutionId))
            return;

        if (pending.ContinuationCase ==
            WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.None)
        {
            var actorCompletion = new CompensationStepCompletedEvent
            {
                RunId = completion.RunId,
                CompensationStepId = completion.StepId,
                Success = completion.Success,
                Error = completion.Error ?? string.Empty,
                ExecutionId = pending.CompensationExecutionId,
            };
            var result = await _stateHost.RecoverCompensationStepCompletionAsync(
                actorCompletion,
                ct);
            result = ResolveRecoveredCompensationTransition(result, completion.Success);
            await StagePendingCompensationContinuationAsync(
                state,
                result,
                completion,
                ctx,
                ct);
        }

        _ = await DrainPendingCompensationContinuationAsync(state, ctx, ct);
    }

    private static WorkflowCompensationTransitionResult ResolveRecoveredCompensationTransition(
        WorkflowCompensationTransitionResult result,
        bool success)
    {
        if (result.Status != WorkflowCompensationTransitionStatus.NoCompensableLedger)
            return result;

        // The kernel only creates this continuation from an actor-issued
        // CompensationRequestEvent. A missing active ledger therefore means the
        // actor already committed the terminal compensation outcome before the
        // interrupted call returned.
        return new WorkflowCompensationTransitionResult(
            success
                ? WorkflowCompensationTransitionStatus.CompletedAll
                : WorkflowCompensationTransitionStatus.CompensationDeadLettered,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static async Task EnsurePendingCompensationOutcomeAsync(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        string compensationExecutionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingCompensationOutcome?.Completion != null)
        {
            if (!state.PendingCompensationOutcome.Completion.Equals(completion) ||
                !string.Equals(
                    state.PendingCompensationOutcome.CompensationExecutionId,
                    compensationExecutionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending compensation outcome for step '{completion.StepId}' changed before commit.");
            }

            return;
        }

        state.PendingCompensationOutcome = new WorkflowPendingCompensationOutcomeState
        {
            Completion = completion.Clone(),
            CompensationExecutionId = compensationExecutionId,
        };
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task StagePendingCompensationContinuationAsync(
        WorkflowExecutionKernelState state,
        WorkflowCompensationTransitionResult result,
        StepCompletedEvent completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var pending = state.PendingCompensationOutcome
            ?? throw new InvalidOperationException("Pending compensation outcome is unavailable.");
        switch (result.Status)
        {
            case WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext:
            case WorkflowCompensationTransitionStatus.Started:
            case WorkflowCompensationTransitionStatus.AlreadyCompensating:
                if (string.IsNullOrWhiteSpace(result.NextCompensationStepId))
                {
                    throw new InvalidOperationException(
                        "A compensation continuation has no next compensation step.");
                }

                pending.NextCompensationRequest = new CompensationRequestEvent
                {
                    RunId = NormalizeRunId(completion.RunId),
                    FailedStepId = string.IsNullOrWhiteSpace(result.FailedStepId)
                        ? completion.StepId ?? string.Empty
                        : result.FailedStepId,
                    CompensationStepId = result.NextCompensationStepId,
                    IdempotencyKey = result.IdempotencyKey ?? string.Empty,
                    CapturedOutput = result.CapturedOutput ?? string.Empty,
                    CapturedOutputValueId = result.CapturedOutputValueId ?? string.Empty,
                    CapturedOutputProducerStepId = result.CapturedOutputProducerStepId ?? string.Empty,
                    CapturedOutputProducerExecutionId = result.CapturedOutputProducerExecutionId ?? string.Empty,
                    CapturedOutputSourceKind = result.CapturedOutputSourceKind,
                    ExecutionId = result.ExecutionId ?? string.Empty,
                };
                break;
            case WorkflowCompensationTransitionStatus.CompletedAll:
            case WorkflowCompensationTransitionStatus.CompensationDeadLettered:
                pending.TerminalCompletion = new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = NormalizeRunId(completion.RunId),
                    Success = false,
                    Error = completion.Error ?? string.Empty,
                    RecoveryFailureKind = state.CompensationTerminalRecoveryFailureKind,
                };
                break;
            case WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate:
                pending.CompletedWithoutContinuation = true;
                break;
            case WorkflowCompensationTransitionStatus.NoCompensableLedger:
            default:
                throw new InvalidOperationException(
                    $"Unsupported compensation continuation status '{result.Status}'.");
        }

        await SaveStateAsync(state, ctx, ct);
    }

    private async Task<bool> DrainPendingCompensationContinuationAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var pending = state.PendingCompensationOutcome;
        if (pending == null)
            return false;

        switch (pending.ContinuationCase)
        {
            case WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.NextCompensationRequest:
                await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await ctx.PublishAsync(
                    pending.NextCompensationRequest.Clone(),
                    TopologyAudience.Self,
                    ct);
                await CompletePendingCompensationOutcomeAsync(
                    LoadState(ctx),
                    pending.Completion.StepId,
                    ctx,
                    ct);
                return true;
            case WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.TerminalCompletion:
                state.CompensationExecutionIdsByStepId.Remove(pending.Completion.StepId);
                await CompleteRunAndPublishAsync(
                    state,
                    pending.TerminalCompletion.Clone(),
                    ctx,
                    ct,
                    preserveCurrentStepInputVariable: true);
                return true;
            case WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.CompletedWithoutContinuation:
                await CompletePendingCompensationOutcomeAsync(
                    state,
                    pending.Completion.StepId,
                    ctx,
                    ct);
                return true;
            default:
                return false;
        }
    }

    private static async Task CompletePendingCompensationOutcomeAsync(
        WorkflowExecutionKernelState state,
        string stepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        state.PendingCompensationOutcome = null;
        state.CompensationExecutionIdsByStepId.Remove(stepId);
        await SaveStateAsync(state, ctx, ct);
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
                {
                    var state = LoadState(ctx);
                    var recoveryFailureKind = state.CompensationTerminalRecoveryFailureKind;
                    await CompleteRunAndPublishAsync(
                        state,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = NormalizeRunId(runId),
                            Success = false,
                            Error = error ?? string.Empty,
                            RecoveryFailureKind = recoveryFailureKind,
                        },
                        ctx,
                        ct,
                        preserveCurrentStepInputVariable: true);
                    return true;
                }
            case WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate:
                return true;
            case WorkflowCompensationTransitionStatus.CompensationDeadLettered:
                {
                    var state = LoadState(ctx);
                    var recoveryFailureKind = state.CompensationTerminalRecoveryFailureKind;
                    var deadLetterError = error ?? string.Empty;
                    await CompleteRunAndPublishAsync(
                        state,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = NormalizeRunId(runId),
                            Success = false,
                            Error = deadLetterError,
                            RecoveryFailureKind = recoveryFailureKind,
                        },
                        ctx,
                        ct,
                        preserveCurrentStepInputVariable: true);
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
                state.CompensationTerminalRecoveryFailureKind = terminalFailure.RecoveryFailureKind;
                await SaveStateAsync(state, ctx, ct);
                await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await PublishCompensationRequestAsync(
                    ctx,
                    terminalFailure.RunId,
                    state.CurrentStepId,
                    result,
                    ct);
                return;
            case WorkflowCompensationTransitionStatus.AlreadyCompensating:
                await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await PublishCompensationRequestAsync(
                    ctx,
                    terminalFailure.RunId,
                    state.CurrentStepId,
                    result,
                    ct);
                return;
            case WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext:
                await SaveStateAsync(state, ctx, ct);
                await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await PublishCompensationRequestAsync(
                    ctx,
                    terminalFailure.RunId,
                    state.CurrentStepId,
                    result,
                    ct);
                return;
            case WorkflowCompensationTransitionStatus.NoCompensableLedger:
                await CompleteRunAndPublishAsync(
                    state,
                    terminalFailure,
                    ctx,
                    ct,
                    preserveCurrentStepInputVariable: true);
                return;
            case WorkflowCompensationTransitionStatus.CompletedAll:
                await CompleteRunAndPublishAsync(
                    state,
                    terminalFailure,
                    ctx,
                    ct,
                    preserveCurrentStepInputVariable: true);
                return;
            case WorkflowCompensationTransitionStatus.CompensationDeadLettered:
                await CompleteRunAndPublishAsync(
                    state,
                    terminalFailure,
                    ctx,
                    ct,
                    preserveCurrentStepInputVariable: true);
                return;
            case WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate:
            default:
                return;
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
            CapturedOutputValueId = result.CapturedOutputValueId ?? string.Empty,
            CapturedOutputProducerStepId = result.CapturedOutputProducerStepId ?? string.Empty,
            CapturedOutputProducerExecutionId = result.CapturedOutputProducerExecutionId ?? string.Empty,
            CapturedOutputSourceKind = result.CapturedOutputSourceKind,
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

        if (evt.RetryDisposition == WorkflowStepRetryDisposition.Forbidden)
        {
            ctx.Logger.LogWarning(
                "workflow_loop: step={StepId} callee forbids an outer retry for this physical outcome",
                step.Id);
            return false;
        }

        if (HasUncertainOutcome(evt))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: step={StepId} outcome is uncertain and is not retried to avoid repeating an external side effect",
                step.Id);
            return false;
        }

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

        var retryInput = WorkflowExecutionValueStore.ResolveCurrentStepInput(state);
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
            await DispatchStepAsync(
                step,
                retryInput,
                state.CurrentStepInputFileRefs,
                state,
                ResolveRetryDispatchKind(state, step.Id),
                ctx,
                ct,
                state.NormalizedValues?.CurrentStepInputValueId);
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

        var retryInput = WorkflowExecutionValueStore.ResolveCurrentStepInput(state);
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
            await DispatchStepAsync(
                step,
                retryInput,
                state.CurrentStepInputFileRefs,
                state,
                dispatchKind,
                ctx,
                ct,
                state.NormalizedValues?.CurrentStepInputValueId);
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
        string completionOutputValueId,
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
                    var outputValueId = WorkflowExecutionValueStore.IsNormalized(state)
                        ? policy.DefaultOutput == null
                            ? completionOutputValueId
                            : WorkflowExecutionValueStore.CaptureInputValue(
                                state,
                                output,
                                WorkflowCanonicalValueSourceKind.ErrorPolicy)
                        : string.Empty;
                    ctx.Logger.LogWarning(
                        "workflow_loop: step={StepId} failed, on_error=skip output=({Len} chars)",
                        step.Id,
                        output.Length);

                    state.RetryAttemptsByStepId.Remove(step.Id);
                    await SaveStateAsync(state, ctx, ct);

                    var next = _workflow.GetNextStep(step.Id);
                    if (next == null)
                    {
                        await CompleteRunAndPublishAsync(
                            state,
                            new WorkflowCompletedEvent
                            {
                                WorkflowName = _workflow.Name,
                                RunId = state.RunId,
                                Success = true,
                                Output = output,
                            },
                            ctx,
                            ct);
                    }
                    else
                    {
                        await DispatchStepAsync(
                            next,
                            output,
                            state.InputFileRefs,
                            state,
                            WorkflowStepDispatchKind.Forward,
                            ctx,
                            ct,
                            outputValueId);
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

                    var fallbackInput = string.IsNullOrWhiteSpace(evt.Output)
                        ? evt.Error ?? string.Empty
                        : evt.Output;
                    var fallbackInputValueId = WorkflowExecutionValueStore.IsNormalized(state)
                        ? string.IsNullOrWhiteSpace(evt.Output)
                            ? WorkflowExecutionValueStore.CaptureInputValue(
                                state,
                                fallbackInput,
                                WorkflowCanonicalValueSourceKind.ErrorPolicy)
                            : completionOutputValueId
                        : string.Empty;
                    state.RetryAttemptsByStepId.Remove(step.Id);
                    await SaveStateAsync(state, ctx, ct);
                    await DispatchStepAsync(
                        fallback,
                        fallbackInput,
                        state.InputFileRefs,
                        state,
                        WorkflowStepDispatchKind.Forward,
                        ctx,
                        ct,
                        fallbackInputValueId);
                    return true;
                }
            default:
                return false;
        }
    }

    private static async Task PrepareWorkflowCompletionAsync(
        WorkflowExecutionKernelState state,
        WorkflowCompletedEvent completed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(completed);

        if (state.PendingWorkflowCompletion != null)
            return;

        state.PendingWorkflowCompletion = completed.Clone();
        await SaveStateAsync(state, ctx, ct);
    }

    private static async Task PublishPreparedWorkflowCompletionAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ctx);
        var pending = state.PendingWorkflowCompletion ??
                      throw new InvalidOperationException("Workflow completion must be persisted before self publication.");

        var durableCompletion = pending.Clone();
        try
        {
            await ctx.PublishAsync(
                durableCompletion,
                TopologyAudience.Self,
                ct,
                BuildWorkflowCompletionPublishOptions(durableCompletion));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IRuntimeEnvelopeRetryableException ||
            WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowDurablePublicationPendingException(
                $"Persisted workflow completion publication remains pending for run '{durableCompletion.RunId}'.",
                ex);
        }
    }

    internal static EventEnvelopePublishOptions BuildWorkflowCompletionPublishOptions(
        WorkflowCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return new EventEnvelopePublishOptions
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    "workflow-completion",
                    completed.RunId ?? string.Empty),
            },
        };
    }

    private async Task CompleteRunAndPublishAsync(
        WorkflowExecutionKernelState state,
        WorkflowCompletedEvent completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        bool preserveTerminalFacts = true,
        bool preserveCurrentStepInputVariable = false)
    {
        await PrepareWorkflowCompletionAsync(state, completion, ctx, ct);
        if (completion.Success)
        {
            await CleanupRunAsync(
                state,
                ctx,
                ct,
                preserveTerminalFacts,
                preserveCurrentStepInputVariable);
        }
        else
        {
            await TryCleanupRunForTerminalFailureAsync(
                state,
                ctx,
                ct,
                preserveTerminalFacts,
                preserveCurrentStepInputVariable);
        }

        await PublishPreparedWorkflowCompletionAsync(state, ctx, ct);
    }

    private async Task TryCleanupRunForTerminalFailureAsync(
        WorkflowExecutionKernelState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        bool preserveTerminalFacts,
        bool preserveCurrentStepInputVariable)
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
        catch (Exception ex) when (
            !ct.IsCancellationRequested &&
            !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
        {
            ctx.Logger.LogError(
                ex,
                "workflow_loop: terminal failure cleanup failed run={RunId} step={StepId}",
                state.RunId,
                state.CurrentStepId);
        }
    }

    private async Task RecoverAcceptedCompletionTransitionAsync(
        WorkflowExecutionKernelState state,
        StepDefinition current,
        StepCompletedEvent completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.NormalizedValues!.CompletedSteps.TryGetValue(
                completion.StepId,
                out var committed))
        {
            return;
        }

        // Failed completions are resumed by their durable retry/backoff or
        // compensation state.  Replaying the completion itself must never
        // start another attempt.
        if (!committed.Success)
            return;

        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            current.ValueLifecycle,
            current.Id,
            completion.ExecutionId,
            _stateHost.IsValuePinnedForCompensation);

        var output = WorkflowExecutionValueStore.GetCanonicalValue(
            state,
            committed.OutputValueId).Value ?? string.Empty;
        StepDefinition? next;
        if (!string.IsNullOrWhiteSpace(committed.NextStepId))
        {
            next = _workflow.GetStep(committed.NextStepId);
            if (next == null)
            {
                await TryStartCompensationOrPublishTerminalFailureAsync(
                    ctx,
                    new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = state.RunId,
                        Success = false,
                        Error = $"invalid next_step '{committed.NextStepId}' from step '{current.Id}'",
                    },
                    state,
                    terminalStep: null,
                    ct);
                return;
            }
        }
        else
        {
            next = _workflow.GetNextStep(current.Id, committed.BranchKey);
        }

        if (next == null)
        {
            await CompleteRunAndPublishAsync(
                state,
                new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = completion.RunId,
                    Success = true,
                    Output = output,
                },
                ctx,
                ct);
            return;
        }

        StageStepDispatch(
            next,
            output,
            state.InputFileRefs,
            state,
            WorkflowStepDispatchKind.Forward,
            ctx,
            committed.OutputValueId);
        await SaveStateAsync(state, ctx, ct);
        await ResumePendingCurrentStepDispatchAsync(state, ctx, ct);
    }

    private void StageStepDispatch(
        StepDefinition step,
        string input,
        IEnumerable<WorkflowFileRef> inputFileRefs,
        WorkflowExecutionKernelState state,
        WorkflowStepDispatchKind dispatchKind,
        IWorkflowExecutionContext ctx,
        string? canonicalInputValueId = null)
    {
        var fileRefs = inputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray();
        WorkflowExecutionValueStore.SetExpressionInput(
            state,
            input,
            canonicalInputValueId);
        _ = ResolveAndPersistStepIdempotency(step, state);

        var effectiveTimeoutMs = NormalizeStepTimeoutMs(ResolveStepTimeoutMs(step, dispatchKind));
        var kernelTimeoutMs = UsesModuleOwnedTimeout(step) ? 0 : effectiveTimeoutMs;
        var timeoutCallbackId = kernelTimeoutMs > 0
            ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
            : string.Empty;
        var executionId = Guid.NewGuid().ToString("N");

        state.ExecutionIdsByStepId[step.Id] = executionId;
        state.CurrentStepId = step.Id;
        WorkflowExecutionValueStore.SetCurrentStepInput(
            state,
            input,
            canonicalInputValueId);
        state.CurrentStepInputFileRefs.Clear();
        state.CurrentStepInputFileRefs.Add(fileRefs.Select(static fileRef => fileRef.Clone()));
        state.CurrentStepDispatchPending = true;
        state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
    }

    private async Task DispatchStepAsync(
        StepDefinition step,
        string input,
        IEnumerable<WorkflowFileRef> inputFileRefs,
        WorkflowExecutionKernelState state,
        WorkflowStepDispatchKind dispatchKind,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        string? canonicalInputValueId = null)
    {
        RuntimeCallbackLease? timeoutLease = null;
        var requestPublishSucceeded = false;
        try
        {
            var fileRefs = inputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray();
            var firstFileRef = fileRefs.FirstOrDefault();
            ctx.Logger.LogWarning(
                "Workflow step input file refs dispatching. runId={RunId} stepId={StepId} stepType={StepType} dispatchKind={DispatchKind} inputFileRefCount={InputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
                state.RunId,
                step.Id,
                WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
                dispatchKind,
                fileRefs.Length,
                firstFileRef?.FileId ?? string.Empty,
                firstFileRef?.ArtifactId ?? string.Empty,
                firstFileRef?.MediaType ?? string.Empty);
            WorkflowExecutionValueStore.SetExpressionInput(
                state,
                input,
                canonicalInputValueId);
            var request = BuildStepRequest(step, input, fileRefs, state, ctx);
            request.InputValueId = WorkflowExecutionValueStore.GetBindingValueId(state, "input")
                ?? string.Empty;
            var idempotency = ResolveAndPersistStepIdempotency(step, state);
            request.IdempotencyKey = idempotency.IdempotencyKey;
            var effectiveTimeoutMs = NormalizeStepTimeoutMs(ResolveStepTimeoutMs(step, dispatchKind));
            request.TimeoutMs = effectiveTimeoutMs;
            var kernelTimeoutMs = UsesModuleOwnedTimeout(step) ? 0 : effectiveTimeoutMs;
            var timeoutCallbackId = kernelTimeoutMs > 0
                ? BuildStepTimeoutCallbackId(state.RunId, step.Id, ResolveInboundEnvelopeId(ctx))
                : string.Empty;

            // Idempotent execution: generate unique execution_id per dispatch
            var executionId = Guid.NewGuid().ToString("N");
            request.ExecutionId = executionId;
            state.ExecutionIdsByStepId[step.Id] = executionId;

            state.CurrentStepId = step.Id;
            WorkflowExecutionValueStore.SetCurrentStepInput(
                state,
                input,
                canonicalInputValueId);
            state.CurrentStepInputFileRefs.Clear();
            state.CurrentStepInputFileRefs.Add(fileRefs.Select(static fileRef => fileRef.Clone()));
            state.CurrentStepDispatchPending = true;
            state.CurrentStepTimeoutCallbackId = timeoutCallbackId;
            await SaveStateAsync(state, ctx, ct);

            timeoutLease = await ScheduleStepTimeoutLeaseAsync(timeoutCallbackId, step, kernelTimeoutMs, state.RunId, ctx, ct);
            if (timeoutLease != null)
            {
                state.TimeoutsByStepId[step.Id] = WorkflowRuntimeCallbackLeaseStateCodec.ToState(timeoutLease);
                await SaveStateAsync(state, ctx, ct);
            }

            await ctx.PublishAsync(request, TopologyAudience.Self, ct);
            requestPublishSucceeded = true;
            await RecordCompensableStepDispatchAsync(step, idempotency, state, ct);

            state.CurrentStepDispatchPending = false;
            await SaveStateAsync(state, ctx, ct);
        }
        catch (Exception ex) when (
            !ct.IsCancellationRequested &&
            !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
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
                catch (Exception saveEx) when (
                    !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(saveEx))
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
        catch (Exception saveEx) when (
            !ct.IsCancellationRequested &&
            !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(saveEx))
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
                ExecutionId = state.ExecutionIdsByStepId.GetValueOrDefault(step.Id, string.Empty),
                Success = false,
                FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                Error = WorkflowRuntimeFailureMessages.StepDispatchFailed(step, exception),
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            },
            ct);
    }

    private async Task RecordCompensableStepDispatchAsync(
        StepDefinition step,
        WorkflowStepIdempotencyState idempotency,
        WorkflowExecutionKernelState state,
        CancellationToken ct)
    {
        var compensationStepId = step.Compensation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compensationStepId) ||
            !WorkflowPrimitiveCatalog.IsSideEffectingPrimitive(step.Type))
        {
            return;
        }

        var dispatched = new CompensableStepDispatchedEvent
        {
            RunId = NormalizeRunId(_stateHost.RunId),
            StepId = step.Id,
            CompensationStepId = compensationStepId,
            IdempotencyKey = idempotency.IdempotencyKey ?? string.Empty,
            DispatchedAtUnixMs = 0,
        };
        var inputValueId = state.NormalizedValues?.CurrentStepInputValueId?.Trim() ?? string.Empty;
        if (inputValueId.Length > 0)
        {
            var inputValue = WorkflowExecutionValueStore.GetCanonicalValue(state, inputValueId);
            dispatched.CapturedOutputValueId = inputValue.ValueId;
            dispatched.CapturedOutputProducerStepId = inputValue.ProducerStepId;
            dispatched.CapturedOutputProducerExecutionId = inputValue.ProducerExecutionId;
            dispatched.CapturedOutputSourceKind = inputValue.SourceKind;
        }

        await _stateHost.RecordCompensableStepDispatchAsync(dispatched, ct);
    }

    private async Task RecordCompensableStepOutcomeAsync(
        StepDefinition step,
        StepCompletedEvent completion,
        string outputValueId,
        WorkflowExecutionKernelState state,
        CancellationToken ct)
    {
        var compensationStepId = step.Compensation?.Trim() ?? string.Empty;
        if (compensationStepId.Length == 0 ||
            !WorkflowPrimitiveCatalog.IsSideEffectingPrimitive(step.Type))
        {
            return;
        }

        var captured = new CompensableStepOutputCapturedEvent
        {
            RunId = NormalizeRunId(_stateHost.RunId),
            StepId = step.Id,
            CompensationStepId = compensationStepId,
            IdempotencyKey = state.IdempotencyByStepId.TryGetValue(step.Id, out var idempotency)
                ? idempotency.IdempotencyKey ?? string.Empty
                : string.Empty,
            Success = completion.Success,
            FailureOutcome = completion.FailureOutcome,
        };
        if (completion.Success)
        {
            var canonical = WorkflowExecutionValueStore.GetCanonicalValue(state, outputValueId);
            captured.CapturedOutputValueId = canonical.ValueId;
            captured.CapturedOutputProducerStepId = canonical.ProducerStepId;
            captured.CapturedOutputProducerExecutionId = canonical.ProducerExecutionId;
            captured.CapturedOutputSourceKind = canonical.SourceKind;
        }

        await _stateHost.RecordCompensableStepOutcomeAsync(captured, ct);
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
        var resolved = _idempotencyKeyResolver.Resolve(step, identity, WorkflowExecutionValueStore.CreateVariableView(state));
        state.IdempotencyByStepId[step.Id] = resolved;
        return resolved;
    }

    private static bool ShouldDeferParameterEvaluation(string canonicalStepType, string parameterKey) =>
        (string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(parameterKey, "condition", StringComparison.OrdinalIgnoreCase)) ||
        ((string.Equals(canonicalStepType, "foreach", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(canonicalStepType, "parallel", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(canonicalStepType, "race", StringComparison.OrdinalIgnoreCase)) &&
         parameterKey.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase));

    private static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        TimeoutErrorPattern.IsMatch(error);

    private static bool HasUncertainOutcome(StepCompletedEvent completion) =>
        completion.FailureOutcome == WorkflowStepFailureOutcome.OutcomeUncertain;

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
        CancellationToken ct,
        bool persistState = true)
    {
        if (MatchesCurrentStep(state, stepId))
            state.CurrentStepTimeoutCallbackId = string.Empty;

        if (!state.TimeoutsByStepId.Remove(stepId, out var lease))
        {
            if (persistState)
                await SaveStateAsync(state, ctx, ct);
            return;
        }

        if (persistState)
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
            ? WorkflowExecutionValueStore.ResolveCurrentStepInput(state)
            : string.Empty;
        var terminalStepInputValueId = preserveTerminalFacts
            ? state.NormalizedValues?.CurrentStepInputValueId
            : null;
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
        if (preserveTerminalFacts)
        {
            WorkflowExecutionValueStore.SetCurrentStepInput(
                state,
                terminalStepInput,
                terminalStepInputValueId);
        }
        else
        {
            state.CurrentStepInput = string.Empty;
            state.NormalizedValues = null;
        }
        state.CurrentStepInputFileRefs.Clear();
        state.CurrentStepInputFileRefs.Add(terminalStepInputFileRefs.Select(static fileRef => fileRef.Clone()));
        state.InputFileRefs.Clear();
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.CompensationPhaseDeadlineCallbackId = string.Empty;
        state.CompensationPhaseDeadlineLease = null;
        state.CompensationTerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        state.PendingCompensationOutcome = null;
        state.Variables.Clear();
        foreach (var (key, value) in terminalVariables)
            state.Variables[key] = value ?? string.Empty;
        if (preserveCurrentStepInputVariable && !string.IsNullOrWhiteSpace(terminalStepInput))
        {
            WorkflowExecutionValueStore.SetExpressionInput(
                state,
                terminalStepInput,
                terminalStepInputValueId);
        }
        if (preserveTerminalFacts)
        {
            state.NormalizedValues?.PendingOutputReferences.Clear();
            state.NormalizedValues?.PendingInternalDispatches.Clear();
            WorkflowExecutionValueStore.PruneUnreferencedValues(state);
        }
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
        if (IsEmptyState(state))
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    internal static bool NormalizeTerminalState(WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.Active = false;
        state.RunId = string.Empty;
        state.CurrentStepDispatchPending = false;
        state.CurrentStepTimeoutCallbackId = string.Empty;
        state.CompensationPhaseDeadlineCallbackId = string.Empty;
        state.CompensationPhaseDeadlineLease = null;
        state.CompensationTerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        state.PendingWorkflowCompletion = null;
        state.InputFileRefs.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.TimeoutsByStepId.Clear();
        state.RetryBackoffsByStepId.Clear();
        state.ExecutionIdsByStepId.Clear();
        state.PendingCompensationOutcome = null;
        state.NormalizedValues?.PendingOutputReferences.Clear();
        state.NormalizedValues?.PendingInternalDispatches.Clear();
        WorkflowExecutionValueStore.PruneUnreferencedValues(state);

        return IsEmptyState(state);
    }

    private static bool IsEmptyState(WorkflowExecutionKernelState state) =>
        !state.Active &&
        string.IsNullOrWhiteSpace(state.RunId) &&
        string.IsNullOrWhiteSpace(state.CurrentStepId) &&
        string.IsNullOrWhiteSpace(state.CurrentStepInput) &&
        !state.CurrentStepDispatchPending &&
        string.IsNullOrWhiteSpace(state.CurrentStepTimeoutCallbackId) &&
        state.Variables.Count == 0 &&
        WorkflowExecutionValueStore.IsEmpty(state) &&
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
        state.PendingCompensationOutcome == null &&
        state.CompensationTerminalRecoveryFailureKind == WorkflowRecoveryFailureKind.Unspecified &&
        state.PendingWorkflowCompletion == null &&
        IsEmptyUsage(state.Usage);

    private static bool MatchesCurrentStep(WorkflowExecutionKernelState state, string? stepId) =>
        !string.IsNullOrWhiteSpace(stepId) &&
        string.Equals(state.CurrentStepId, stepId, StringComparison.Ordinal);

    // Digest replay evidence (and the raw-payload pruning it permits) is a schema-v2 fact.
    // Only the runtime-owned adoption receipt may switch it on; a v1-identity actor keeps
    // raw values so an older reader can still validate exact replay.
    private WorkflowValueReplayEvidence ResolveReplayEvidence() =>
        WorkflowNormalizedStateWriteAdmission.IsValueLifecycleGranted(_stateHost.RuntimeStateSchemaContextReader)
            ? WorkflowValueReplayEvidence.Digest
            : WorkflowValueReplayEvidence.RawValue;

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

    private static bool HasWorkflowCallInvocationId(StartWorkflowEvent evt) =>
        evt.Parameters.TryGetValue(WorkflowCallInvocationIdParameterKey, out var invocationId) &&
        !string.IsNullOrWhiteSpace(invocationId);

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
        if (!WorkflowExecutionValueStore.IsNormalized(state))
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
        WorkflowExecutionValueStore.ReleaseRunUsageBindings(state);
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
            DisplayName = ResolveStepDisplayName(step),
        };
        request.InputFileRefs.Add(inputFileRefs.Select(static fileRef => fileRef.Clone()));

        foreach (var (key, value) in step.Parameters)
        {
            if (ShouldDeferParameterEvaluation(canonicalStepType, key))
            {
                request.Parameters[key] = value;
                continue;
            }

            var evaluated = _expressionEvaluator.Evaluate(value, WorkflowExecutionValueStore.CreateVariableView(state));
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

    private static string ResolveStepDisplayName(StepDefinition step)
    {
        var displayName = step.DisplayName?.Trim() ?? string.Empty;
        return displayName.Length == 0 ? step.Id : displayName;
    }

    // The call-site identity a step carries at runtime must be the one admission committed, so both
    // sides derive it from the compiler. Composite primitives receive their synthesized sub-step
    // call site here and copy it onto every child they dispatch.
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
        spec.Key = _expressionEvaluator.Evaluate(spec.Key, WorkflowExecutionValueStore.CreateVariableView(state));
        spec.Value = _expressionEvaluator.Evaluate(spec.Value, WorkflowExecutionValueStore.CreateVariableView(state));
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
            : _expressionEvaluator.Evaluate(value, WorkflowExecutionValueStore.CreateVariableView(state)).Trim();

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
            _expressionEvaluator.Evaluate(presentation.DeliveryTargetId.Trim(), WorkflowExecutionValueStore.CreateVariableView(state));
    }

    private void ApplyInteractionSpec(
        StepRequestEvent request,
        StepPresentation? presentation,
        WorkflowExecutionKernelState state)
    {
        if (!StepPresentation.HasInteractionSpec(presentation?.InteractionSpec))
            return;

        var spec = presentation!.InteractionSpec!.Clone();
        spec.Title = _expressionEvaluator.Evaluate(spec.Title, WorkflowExecutionValueStore.CreateVariableView(state));
        spec.Body = _expressionEvaluator.Evaluate(spec.Body, WorkflowExecutionValueStore.CreateVariableView(state));
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
        spec.TemplateId = _expressionEvaluator.Evaluate(spec.TemplateId, WorkflowExecutionValueStore.CreateVariableView(state));
        var evaluatedVariables = spec.TemplateVariable
            .Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                _expressionEvaluator.Evaluate(pair.Value, WorkflowExecutionValueStore.CreateVariableView(state))))
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
            action.Label = _expressionEvaluator.Evaluate(action.Label, WorkflowExecutionValueStore.CreateVariableView(state));
            action.Value = _expressionEvaluator.Evaluate(action.Value, WorkflowExecutionValueStore.CreateVariableView(state));
            action.Placeholder = _expressionEvaluator.Evaluate(action.Placeholder, WorkflowExecutionValueStore.CreateVariableView(state));
            foreach (var option in action.Options)
            {
                option.Label = _expressionEvaluator.Evaluate(option.Label, WorkflowExecutionValueStore.CreateVariableView(state));
                option.Value = _expressionEvaluator.Evaluate(option.Value, WorkflowExecutionValueStore.CreateVariableView(state));
            }
        }
    }

    private void EvaluateFields(
        IEnumerable<Aevatar.Foundation.Abstractions.Interactions.InteractionField> fields,
        WorkflowExecutionKernelState state)
    {
        foreach (var field in fields)
        {
            field.Title = _expressionEvaluator.Evaluate(field.Title, WorkflowExecutionValueStore.CreateVariableView(state));
            field.Text = _expressionEvaluator.Evaluate(field.Text, WorkflowExecutionValueStore.CreateVariableView(state));
        }
    }

    private void EvaluateCards(
        IEnumerable<Aevatar.Foundation.Abstractions.Interactions.InteractionCard> cards,
        WorkflowExecutionKernelState state)
    {
        foreach (var card in cards)
        {
            card.Title = _expressionEvaluator.Evaluate(card.Title, WorkflowExecutionValueStore.CreateVariableView(state));
            card.Text = _expressionEvaluator.Evaluate(card.Text, WorkflowExecutionValueStore.CreateVariableView(state));
            card.ImageUrl = _expressionEvaluator.Evaluate(card.ImageUrl, WorkflowExecutionValueStore.CreateVariableView(state));
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

        var request = BuildStepRequest(
            step,
            WorkflowExecutionValueStore.ResolveCurrentStepInput(state),
            state.CurrentStepInputFileRefs,
            state,
            ctx);
        request.TimeoutMs = NormalizeStepTimeoutMs(
            ResolveStepTimeoutMs(step, ResolveRetryDispatchKind(state, step.Id)));

        // Restore the saved execution_id so stale-completion protection works after resume
        if (state.ExecutionIdsByStepId.TryGetValue(step.Id, out var savedExecutionId))
            request.ExecutionId = savedExecutionId;
        request.InputValueId = state.NormalizedValues?.CurrentStepInputValueId ?? string.Empty;
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

    private static int NormalizeStepTimeoutMs(int timeoutMs) =>
        timeoutMs > 0 ? Math.Clamp(timeoutMs, 100, 600_000) : 0;

    private static bool UsesModuleOwnedTimeout(StepDefinition step) =>
        string.Equals(
            WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
            "tool_call",
            StringComparison.OrdinalIgnoreCase);

    private static WorkflowStepDispatchKind ResolveRetryDispatchKind(
        WorkflowExecutionKernelState state,
        string stepId) =>
        state.CompensationExecutionIdsByStepId.ContainsKey(stepId)
            ? WorkflowStepDispatchKind.Compensation
            : WorkflowStepDispatchKind.Forward;

}
