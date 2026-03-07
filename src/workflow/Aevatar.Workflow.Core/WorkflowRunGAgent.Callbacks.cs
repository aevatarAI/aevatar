using System.Globalization;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private async Task HandleWorkflowStepTimeoutFiredAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!WorkflowRunSupport.TryMatchRunAndStep(State.RunId, evt.RunId, evt.StepId))
            return;
        if (!State.PendingTimeouts.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = State.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private async Task HandleWorkflowStepRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!WorkflowRunSupport.TryMatchRunAndStep(State.RunId, evt.RunId, evt.StepId))
            return;
        if (!State.PendingRetryBackoffs.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var step = _compiledWorkflow?.GetStep(evt.StepId);
        if (step == null || !State.StepExecutions.TryGetValue(evt.StepId, out var execution))
            return;

        var next = State.Clone();
        next.PendingRetryBackoffs.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, State.RunId, ct);
    }

    private async Task HandleDelayStepTimeoutFiredAsync(
        DelayStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!WorkflowRunSupport.TryMatchRunAndStep(State.RunId, evt.RunId, evt.StepId))
            return;
        if (!State.PendingDelays.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = State.Clone();
        next.PendingDelays.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    private async Task HandleWaitSignalTimeoutFiredAsync(
        WaitSignalTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!WorkflowRunSupport.TryMatchRunAndStep(State.RunId, evt.RunId, evt.StepId))
            return;
        if (!State.PendingSignalWaits.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.TimeoutGeneration))
            return;

        var next = State.Clone();
        next.PendingSignalWaits.Remove(evt.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"signal '{pending.SignalName}' timed out after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private async Task HandleLlmCallWatchdogTimeoutFiredAsync(
        LlmCallWatchdogTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), State.RunId, StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(evt.SessionId) || !State.PendingLlmCalls.TryGetValue(evt.SessionId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.WatchdogGeneration))
            return;

        var next = State.Clone();
        next.PendingLlmCalls.Remove(evt.SessionId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"LLM call timed out after {evt.TimeoutMs}ms",
            WorkerId = string.IsNullOrWhiteSpace(pending.TargetRole) ? Id : pending.TargetRole,
        }, EventDirection.Self, ct);
    }

    private async Task HandleLlmLikeResponseAsync(string? sessionId, string content, string publisherId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (State.PendingLlmCalls.TryGetValue(sessionId, out var llmPending))
        {
            var next = State.Clone();
            next.PendingLlmCalls.Remove(sessionId);
            await PersistStateAsync(next, ct);

            if (WorkflowRunSupport.TryExtractLlmFailure(content, out var llmError))
            {
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = llmPending.StepId,
                    RunId = State.RunId,
                    Success = false,
                    Error = llmError,
                    WorkerId = string.IsNullOrWhiteSpace(publisherId) ? Id : publisherId,
                }, EventDirection.Self, ct);
                return;
            }

            await PublishAsync(new StepCompletedEvent
            {
                StepId = llmPending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = content,
                WorkerId = string.IsNullOrWhiteSpace(publisherId) ? Id : publisherId,
            }, EventDirection.Self, ct);
            return;
        }

        if (State.PendingEvaluations.TryGetValue(sessionId, out var evalPending))
        {
            var score = WorkflowRunSupport.ParseScore(content);
            var passed = score >= evalPending.Threshold;
            var next = State.Clone();
            next.PendingEvaluations.Remove(sessionId);
            await PersistStateAsync(next, ct);

            var completed = new StepCompletedEvent
            {
                StepId = evalPending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = evalPending.OriginalInput,
            };
            completed.Metadata["evaluate.score"] = score.ToString("F1", CultureInfo.InvariantCulture);
            completed.Metadata["evaluate.passed"] = passed.ToString();
            if (!passed && !string.IsNullOrWhiteSpace(evalPending.OnBelow))
                completed.Metadata["branch"] = evalPending.OnBelow;
            await PublishAsync(completed, EventDirection.Self, ct);
            return;
        }

        if (!State.PendingReflections.TryGetValue(sessionId, out var reflectPending))
            return;

        var reflectNext = State.Clone();
        reflectNext.PendingReflections.Remove(sessionId);
        await PersistStateAsync(reflectNext, ct);

        if (string.Equals(reflectPending.Phase, "critique", StringComparison.OrdinalIgnoreCase))
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = reflectPending.Round + 1;
            if (passed || round >= reflectPending.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = reflectPending.StepId,
                    RunId = State.RunId,
                    Success = true,
                    Output = reflectPending.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString(CultureInfo.InvariantCulture);
                completed.Metadata["reflect.passed"] = passed.ToString();
                await PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var nextPending = reflectPending.Clone();
            nextPending.Round = round;
            nextPending.Phase = "improve";
            await DispatchReflectPhaseAsync(State.RunId, nextPending, content, ct);
            return;
        }

        var critiquePending = reflectPending.Clone();
        critiquePending.CurrentDraft = content;
        critiquePending.Phase = "critique";
        await DispatchReflectPhaseAsync(State.RunId, critiquePending, content, ct);
    }

    private async Task DispatchReflectPhaseAsync(
        string runId,
        WorkflowPendingReflectState pending,
        string content,
        CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var prompt = string.Equals(pending.Phase, "critique", StringComparison.OrdinalIgnoreCase)
            ? $"""
                Review the following content against these criteria: {pending.Criteria}
                If the content meets the criteria, respond with exactly "PASS".
                Otherwise, explain what needs improvement.

                Content:
                {content}
                """
            : $"""
                Improve the following content based on this feedback.

                Feedback:
                {content}

                Original content:
                {pending.CurrentDraft}
                """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(
            Id,
            runId,
            $"{pending.StepId}_r{pending.Round}_{pending.Phase}");
        var nextPending = pending.Clone();
        nextPending.SessionId = sessionId;

        var next = State.Clone();
        next.PendingReflections[sessionId] = nextPending;
        await PersistStateAsync(next, ct);

        var request = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(nextPending.TargetRole))
        {
            await SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(Id, nextPending.TargetRole),
                request,
                ct);
            return;
        }

        await PublishAsync(request, EventDirection.Self, ct);
    }

    private async Task<bool> TryScheduleRetryAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        var policy = step.Retry;
        if (policy == null)
            return false;

        if (WorkflowRunSupport.IsTimeoutError(evt.Error))
            return false;

        var scheduledRetryCount = State.RetryAttemptsByStepId.TryGetValue(step.Id, out var existingRetryCount)
            ? existingRetryCount
            : 0;
        var nextRetryCount = scheduledRetryCount + 1;
        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        if (nextRetryCount >= maxAttempts)
            return false;

        if (!State.StepExecutions.TryGetValue(step.Id, out var execution))
            return false;

        next.RetryAttemptsByStepId[step.Id] = nextRetryCount;
        var delayMs = string.Equals(policy.Backoff, "exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (nextRetryCount - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        if (delayMs <= 0)
        {
            await PersistStateAsync(next, ct);
            await DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, State.RunId, ct);
            return true;
        }

        next.PendingRetryBackoffs[step.Id] = new WorkflowPendingRetryBackoffState
        {
            StepId = step.Id,
            DelayMs = delayMs,
            NextAttempt = nextRetryCount + 1,
            SemanticGeneration = WorkflowRunSupport.NextSemanticGeneration(
                State.PendingRetryBackoffs.TryGetValue(step.Id, out var existingBackoff)
                    ? existingBackoff.SemanticGeneration
                    : 0),
        };
        await PersistStateAsync(next, ct);
        await ScheduleWorkflowCallbackAsync(
            WorkflowRunSupport.BuildRetryBackoffCallbackId(State.RunId, step.Id),
            TimeSpan.FromMilliseconds(delayMs),
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = State.RunId,
                StepId = step.Id,
                DelayMs = delayMs,
                NextAttempt = nextRetryCount + 1,
            },
            next.PendingRetryBackoffs[step.Id].SemanticGeneration,
            step.Id,
            sessionId: null,
            kind: "retry_backoff",
            ct);
        return true;
    }

    private async Task<bool> TryHandleOnErrorAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        var policy = step.OnError;
        if (policy == null)
            return false;

        switch ((policy.Strategy ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "skip":
            {
                var output = policy.DefaultOutput ?? evt.Output ?? string.Empty;
                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                var nextStep = _compiledWorkflow!.GetNextStep(step.Id);
                await PersistStateAsync(next, ct);
                if (nextStep == null)
                {
                    await FinalizeRunAsync(true, output, string.Empty, ct);
                    return true;
                }

                await DispatchWorkflowStepAsync(nextStep, output, State.RunId, ct);
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = _compiledWorkflow!.GetStep(policy.FallbackStep);
                if (fallback == null)
                    return false;

                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                await PersistStateAsync(next, ct);
                await DispatchWorkflowStepAsync(fallback, evt.Output ?? string.Empty, State.RunId, ct);
                return true;
            }
            default:
                return false;
        }
    }
}
