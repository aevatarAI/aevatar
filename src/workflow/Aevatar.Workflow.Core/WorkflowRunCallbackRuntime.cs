using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunCallbackRuntime
{
    private readonly WorkflowRunRuntimeContext _context;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;
    private readonly WorkflowRunAIRuntime _aiRuntime;

    public WorkflowRunCallbackRuntime(
        WorkflowRunRuntimeContext context,
        WorkflowRunDispatchRuntime dispatchRuntime,
        WorkflowRunAIRuntime aiRuntime)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatchRuntime = dispatchRuntime ?? throw new ArgumentNullException(nameof(dispatchRuntime));
        _aiRuntime = aiRuntime ?? throw new ArgumentNullException(nameof(aiRuntime));
    }

    public async Task HandleWorkflowStepTimeoutFiredAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingTimeouts.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    public async Task HandleWorkflowStepRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingRetryBackoffs.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var step = _context.CompiledWorkflow?.GetStep(evt.StepId);
        if (step == null || !state.StepExecutions.TryGetValue(evt.StepId, out var execution))
            return;

        var next = state.Clone();
        next.PendingRetryBackoffs.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _dispatchRuntime.DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, state.RunId, ct);
    }

    public async Task HandleDelayStepTimeoutFiredAsync(
        DelayStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingDelays.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingDelays.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    public async Task HandleWaitSignalTimeoutFiredAsync(
        WaitSignalTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingSignalWaits.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.TimeoutGeneration))
            return;

        var next = state.Clone();
        next.PendingSignalWaits.Remove(evt.StepId);
        next.Status = "active";
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"signal '{pending.SignalName}' timed out after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    public async Task HandleLlmCallWatchdogTimeoutFiredAsync(
        LlmCallWatchdogTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), state.RunId, StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(evt.SessionId) || !state.PendingLlmCalls.TryGetValue(evt.SessionId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.WatchdogGeneration))
            return;

        var next = state.Clone();
        next.PendingLlmCalls.Remove(evt.SessionId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"LLM call timed out after {evt.TimeoutMs}ms",
            WorkerId = string.IsNullOrWhiteSpace(pending.TargetRole) ? _context.ActorId : pending.TargetRole,
        }, EventDirection.Self, ct);
    }

    public async Task HandleLlmLikeResponseAsync(
        string? sessionId,
        string content,
        string publisherId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var state = _context.State;
        if (state.PendingLlmCalls.TryGetValue(sessionId, out var llmPending))
        {
            var next = state.Clone();
            next.PendingLlmCalls.Remove(sessionId);
            await _context.PersistStateAsync(next, ct);

            if (WorkflowRunSupport.TryExtractLlmFailure(content, out var llmError))
            {
                await _context.PublishAsync(new StepCompletedEvent
                {
                    StepId = llmPending.StepId,
                    RunId = state.RunId,
                    Success = false,
                    Error = llmError,
                    WorkerId = string.IsNullOrWhiteSpace(publisherId) ? _context.ActorId : publisherId,
                }, EventDirection.Self, ct);
                return;
            }

            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = llmPending.StepId,
                RunId = state.RunId,
                Success = true,
                Output = content,
                WorkerId = string.IsNullOrWhiteSpace(publisherId) ? _context.ActorId : publisherId,
            }, EventDirection.Self, ct);
            return;
        }

        if (state.PendingEvaluations.TryGetValue(sessionId, out var evalPending))
        {
            var score = WorkflowRunSupport.ParseScore(content);
            var passed = score >= evalPending.Threshold;
            var next = state.Clone();
            next.PendingEvaluations.Remove(sessionId);
            await _context.PersistStateAsync(next, ct);

            var completed = new StepCompletedEvent
            {
                StepId = evalPending.StepId,
                RunId = state.RunId,
                Success = true,
                Output = evalPending.OriginalInput,
            };
            completed.Metadata["evaluate.score"] = score.ToString("F1", CultureInfo.InvariantCulture);
            completed.Metadata["evaluate.passed"] = passed.ToString();
            if (!passed && !string.IsNullOrWhiteSpace(evalPending.OnBelow))
                completed.Metadata["branch"] = evalPending.OnBelow;
            await _context.PublishAsync(completed, EventDirection.Self, ct);
            return;
        }

        if (!state.PendingReflections.TryGetValue(sessionId, out var reflectPending))
            return;

        var reflectNext = state.Clone();
        reflectNext.PendingReflections.Remove(sessionId);
        await _context.PersistStateAsync(reflectNext, ct);

        if (string.Equals(reflectPending.Phase, "critique", StringComparison.OrdinalIgnoreCase))
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = reflectPending.Round + 1;
            if (passed || round >= reflectPending.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = reflectPending.StepId,
                    RunId = state.RunId,
                    Success = true,
                    Output = reflectPending.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString(CultureInfo.InvariantCulture);
                completed.Metadata["reflect.passed"] = passed.ToString();
                await _context.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var nextPending = reflectPending.Clone();
            nextPending.Round = round;
            nextPending.Phase = "improve";
            await _aiRuntime.DispatchReflectPhaseAsync(state.RunId, nextPending, content, ct);
            return;
        }

        var critiquePending = reflectPending.Clone();
        critiquePending.CurrentDraft = content;
        critiquePending.Phase = "critique";
        await _aiRuntime.DispatchReflectPhaseAsync(state.RunId, critiquePending, content, ct);
    }
}
