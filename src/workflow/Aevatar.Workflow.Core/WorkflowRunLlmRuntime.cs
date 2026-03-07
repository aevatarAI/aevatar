using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunLlmRuntime
    : IWorkflowStepFamilyHandler, IWorkflowInternalSignalHandler
{
    private static readonly string[] SupportedTypes = ["llm_call"];

    private readonly WorkflowRunRuntimeContext _context;

    public WorkflowRunLlmRuntime(WorkflowRunRuntimeContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyCollection<string> SupportedStepTypes => SupportedTypes;

    public Task HandleStepRequestAsync(StepRequestEvent request, CancellationToken ct) =>
        HandleLlmCallStepRequestAsync(request, ct);

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor) == true;

    public Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload?.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor) != true)
            return Task.CompletedTask;

        return HandleLlmCallWatchdogTimeoutFiredAsync(
            payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>(),
            envelope,
            ct);
    }

    public async Task HandleLlmCallStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = "llm_call step requires non-empty run_id and step_id",
            }, EventDirection.Self, ct);
            return;
        }

        await _context.EnsureAgentTreeAsync(ct);

        var prompt = request.Input ?? string.Empty;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) && !string.IsNullOrWhiteSpace(prefix))
            prompt = prefix.TrimEnd() + "\n\n" + prompt;

        var state = _context.State;
        var attempt = state.StepExecutions.TryGetValue(stepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var timeoutMs = WorkflowRunSupport.ResolveLlmTimeoutMs(request.Parameters);
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(_context.ActorId, runId, stepId, attempt);
        var next = state.Clone();
        next.PendingLlmCalls[sessionId] = new WorkflowPendingLlmCallState
        {
            SessionId = sessionId,
            StepId = stepId,
            OriginalInput = request.Input ?? string.Empty,
            TargetRole = request.TargetRole ?? string.Empty,
            TimeoutMs = timeoutMs,
            WatchdogGeneration = WorkflowRunSupport.NextSemanticGeneration(
                state.PendingLlmCalls.TryGetValue(sessionId, out var existing) ? existing.WatchdogGeneration : 0),
            Attempt = attempt,
        };
        await _context.PersistStateAsync(next, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        chatRequest.Metadata["aevatar.llm_timeout_ms"] = timeoutMs.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.TargetRole))
            {
                await _context.SendToAsync(
                    WorkflowRoleActorIdResolver.ResolveTargetActorId(_context.ActorId, request.TargetRole),
                    chatRequest,
                    ct);
            }
            else
            {
                await _context.PublishAsync(chatRequest, EventDirection.Self, ct);
            }
        }
        catch (Exception ex)
        {
            await RemovePendingLlmCallAndPublishFailureAsync(sessionId, stepId, runId, $"LLM dispatch failed: {ex.Message}", ct);
            return;
        }

        try
        {
            await _context.ScheduleWorkflowCallbackAsync(
                WorkflowRunSupport.BuildLlmWatchdogCallbackId(sessionId),
                TimeSpan.FromMilliseconds(timeoutMs),
                new LlmCallWatchdogTimeoutFiredEvent
                {
                    SessionId = sessionId,
                    TimeoutMs = timeoutMs,
                    RunId = runId,
                    StepId = stepId,
                },
                next.PendingLlmCalls[sessionId].WatchdogGeneration,
                stepId,
                sessionId,
                "llm_watchdog",
                ct);
        }
        catch (Exception ex)
        {
            await RemovePendingLlmCallAndPublishFailureAsync(sessionId, stepId, runId, $"LLM watchdog scheduling failed: {ex.Message}", ct);
        }
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

    public async Task<bool> TryHandleLlmLikeResponseAsync(
        string sessionId,
        string content,
        string publisherId,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!state.PendingLlmCalls.TryGetValue(sessionId, out var pending))
            return false;

        var next = state.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await _context.PersistStateAsync(next, ct);

        if (WorkflowRunSupport.TryExtractLlmFailure(content, out var llmError))
        {
            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = state.RunId,
                Success = false,
                Error = llmError,
                WorkerId = string.IsNullOrWhiteSpace(publisherId) ? _context.ActorId : publisherId,
            }, EventDirection.Self, ct);
            return true;
        }

        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = state.RunId,
            Success = true,
            Output = content,
            WorkerId = string.IsNullOrWhiteSpace(publisherId) ? _context.ActorId : publisherId,
        }, EventDirection.Self, ct);
        return true;
    }

    private async Task RemovePendingLlmCallAndPublishFailureAsync(
        string sessionId,
        string stepId,
        string runId,
        string error,
        CancellationToken ct)
    {
        var next = _context.State.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }
}
