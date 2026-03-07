using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowLlmCallCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "llm_call",
        SupportedStepTypes: ["llm_call"],
        SupportedInternalSignalTypeUrls:
        [
            WorkflowCapabilityRoutes.For<LlmCallWatchdogTimeoutFiredEvent>(),
        ],
        SupportedResponseTypeUrls:
        [
            WorkflowCapabilityRoutes.For<TextMessageEndEvent>(),
            WorkflowCapabilityRoutes.For<ChatResponseEvent>(),
        ]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public async Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = "llm_call step requires non-empty run_id and step_id",
            }, EventDirection.Self, ct);
            return;
        }

        await effects.EnsureAgentTreeAsync(ct);

        var prompt = request.Input ?? string.Empty;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) && !string.IsNullOrWhiteSpace(prefix))
            prompt = prefix.TrimEnd() + "\n\n" + prompt;

        var state = read.State;
        var attempt = state.StepExecutions.TryGetValue(stepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var timeoutMs = WorkflowCapabilityValueParsers.ResolveLlmTimeoutMs(request.Parameters);
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(read.ActorId, runId, stepId, attempt);
        var next = state.Clone();
        next.PendingLlmCalls[sessionId] = new WorkflowPendingLlmCallState
        {
            SessionId = sessionId,
            StepId = stepId,
            OriginalInput = request.Input ?? string.Empty,
            TargetRole = request.TargetRole ?? string.Empty,
            TimeoutMs = timeoutMs,
            WatchdogGeneration = WorkflowSemanticGeneration.Next(
                state.PendingLlmCalls.TryGetValue(sessionId, out var existing) ? existing.WatchdogGeneration : 0),
            Attempt = attempt,
        };
        await write.PersistStateAsync(next, ct);

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
                await write.SendToAsync(
                    WorkflowRoleActorIdResolver.ResolveTargetActorId(read.ActorId, request.TargetRole),
                    chatRequest,
                    ct);
            }
            else
            {
                await write.PublishAsync(chatRequest, EventDirection.Self, ct);
            }
        }
        catch (Exception ex)
        {
            await RemovePendingAndPublishFailureAsync(sessionId, stepId, runId, $"LLM dispatch failed: {ex.Message}", read, write, ct);
            return;
        }

        try
        {
            await effects.ScheduleWorkflowCallbackAsync(
                WorkflowCallbackKeys.BuildLlmWatchdogCallbackId(sessionId),
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
            await RemovePendingAndPublishFailureAsync(sessionId, stepId, runId, $"LLM watchdog scheduling failed: {ex.Message}", read, write, ct);
        }
    }

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read)
    {
        var payload = envelope.Payload;
        if (payload?.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor) != true)
            return false;

        var evt = payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>();
        return !string.IsNullOrWhiteSpace(evt.SessionId) && read.State.PendingLlmCalls.ContainsKey(evt.SessionId);
    }

    public async Task HandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var evt = envelope.Payload!.Unpack<LlmCallWatchdogTimeoutFiredEvent>();
        var state = read.State;
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), state.RunId, StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(evt.SessionId) || !state.PendingLlmCalls.TryGetValue(evt.SessionId, out var pending))
            return;
        if (!WorkflowSemanticGeneration.Matches(envelope, pending.WatchdogGeneration))
            return;

        var next = state.Clone();
        next.PendingLlmCalls.Remove(evt.SessionId);
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"LLM call timed out after {evt.TimeoutMs}ms",
            WorkerId = string.IsNullOrWhiteSpace(pending.TargetRole) ? read.ActorId : pending.TargetRole,
        }, EventDirection.Self, ct);
    }

    public bool CanHandleResponse(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read)
    {
        return TryExtractResponseSession(envelope, out var sessionId, out _, out _) &&
               !string.IsNullOrWhiteSpace(sessionId) &&
               read.State.PendingLlmCalls.ContainsKey(sessionId);
    }

    public async Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (!TryExtractResponseSession(envelope, out var sessionId, out var content, out var publisherId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var state = read.State;
        if (!state.PendingLlmCalls.TryGetValue(sessionId, out var pending))
            return;

        var next = state.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await write.PersistStateAsync(next, ct);

        if (WorkflowCapabilityValueParsers.TryExtractLlmFailure(content, out var llmError))
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = state.RunId,
                Success = false,
                Error = llmError,
                WorkerId = string.IsNullOrWhiteSpace(publisherId) ? read.ActorId : publisherId,
            }, EventDirection.Self, ct);
            return;
        }

        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = state.RunId,
            Success = true,
            Output = content,
            WorkerId = string.IsNullOrWhiteSpace(publisherId) ? read.ActorId : publisherId,
        }, EventDirection.Self, ct);
    }

    public bool CanHandleChildRunCompletion(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static bool TryExtractResponseSession(
        EventEnvelope envelope,
        out string sessionId,
        out string content,
        out string publisherId)
    {
        sessionId = string.Empty;
        content = string.Empty;
        publisherId = string.IsNullOrWhiteSpace(envelope.PublisherId) ? string.Empty : envelope.PublisherId;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            sessionId = evt.SessionId ?? string.Empty;
            content = evt.Content ?? string.Empty;
            return true;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            sessionId = evt.SessionId ?? string.Empty;
            content = evt.Content ?? string.Empty;
            return true;
        }

        return false;
    }

    private static async Task RemovePendingAndPublishFailureAsync(
        string sessionId,
        string stepId,
        string runId,
        string error,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var next = read.State.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }
}
