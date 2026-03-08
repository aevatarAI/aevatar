using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends ChatRequestEvent to a specific RoleGAgent by ID.</summary>
public sealed class LLMCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const int DefaultLlmTimeoutMs = 1_800_000;
    private const string LlmTimeoutMetadataKey = "aevatar.llm_timeout_ms";
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string LlmWatchdogCallbackPrefix = "llm-watchdog";
    private const string ModuleStateKey = "llm_call";

    public string Name => "llm_call";
    public int Priority => 10;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor)
                || payload.Is(TextMessageEndEvent.Descriptor)
                || payload.Is(ChatResponseEvent.Descriptor)
                || payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            await HandleStepRequestAsync(payload.Unpack<StepRequestEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            await HandleTextMessageEndAsync(payload.Unpack<TextMessageEndEvent>(), envelope, ctx, ct);
            return;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            await HandleChatResponseAsync(payload.Unpack<ChatResponseEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor))
            await HandleWatchdogTimeoutFiredAsync(payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>(), envelope, ctx, ct);
    }

    private async Task HandleStepRequestAsync(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (request.StepType != "llm_call")
            return;

        var prompt = request.Input;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) &&
            !string.IsNullOrEmpty(prefix))
        {
            prompt = prefix.TrimEnd() + "\n\n" + prompt;
        }

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await PublishFailedCompletionAsync(
                new PendingLlmCallState
                {
                    StepId = stepId,
                    RunId = runId,
                    TargetRole = request.TargetRole ?? string.Empty,
                },
                "llm_call step requires non-empty run_id and step_id",
                ctx.AgentId,
                ctx,
                ct);
            return;
        }

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        var attempt = runtimeState.AttemptsByStepId.GetValueOrDefault(stepId, 0) + 1;
        runtimeState.AttemptsByStepId[stepId] = attempt;

        var chatSessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, runId, stepId, attempt);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var watchdogCallbackId = BuildWatchdogCallbackId(chatSessionId);
        runtimeState.PendingBySessionId[chatSessionId] = new PendingLlmCallState
        {
            StepId = stepId,
            RunId = runId,
            TargetRole = request.TargetRole ?? string.Empty,
        };
        await SaveStateAsync(runtimeState, ctx, ct);

        var targetRole = request.TargetRole;
        var promptPreview = prompt.Length > 200 ? prompt[..200] + "..." : prompt;
        var chatEvt = new ChatRequestEvent { Prompt = prompt, SessionId = chatSessionId };
        chatEvt.Metadata[LlmTimeoutMetadataKey] = timeoutMs.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (!string.IsNullOrEmpty(targetRole))
            {
                var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, targetRole);
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → SendTo role={Role} actor={ActorId} timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    stepId, targetRole, targetActorId, timeoutMs, prompt.Length, promptPreview);
                await ctx.SendToAsync(targetActorId, chatEvt, ct);
            }
            else
            {
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → Self (no role) timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    stepId, timeoutMs, prompt.Length, promptPreview);
                await ctx.PublishAsync(chatEvt, EventDirection.Self, ct);
            }

            var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                watchdogCallbackId,
                TimeSpan.FromMilliseconds(timeoutMs),
                new LlmCallWatchdogTimeoutFiredEvent
                {
                    SessionId = chatSessionId,
                    TimeoutMs = timeoutMs,
                    RunId = runId,
                    StepId = stepId,
                },
                ct: ct);

            runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
            if (runtimeState.PendingBySessionId.TryGetValue(chatSessionId, out var pendingState))
            {
                pendingState.WatchdogLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
                runtimeState.PendingBySessionId[chatSessionId] = pendingState;
                await SaveStateAsync(runtimeState, ctx, ct);
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: dispatch failed for step={StepId}", stepId);
            await FailPendingAsync(chatSessionId, $"LLM dispatch failed: {ex.Message}", ctx.AgentId, ctx, ct);
        }
    }

    private async Task HandleTextMessageEndAsync(
        TextMessageEndEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        runtimeState.AttemptsByStepId.Remove(pending.StepId);
        await SaveStateAsync(runtimeState, ctx, ct);

        var outputPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending, error, envelope.PublisherId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed ({Len} chars): {Preview}",
            pending.StepId, evt.Content?.Length ?? 0, outputPreview);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = true,
            Output = evt.Content ?? string.Empty,
            WorkerId = envelope.PublisherId,
        }, EventDirection.Self, ct);
    }

    private async Task HandleChatResponseAsync(
        ChatResponseEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        runtimeState.AttemptsByStepId.Remove(pending.StepId);
        await SaveStateAsync(runtimeState, ctx, ct);

        var nsPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending, error, ctx.AgentId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed non-streaming ({Len} chars): {Preview}",
            pending.StepId, evt.Content?.Length ?? 0, nsPreview);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = true,
            Output = evt.Content ?? string.Empty,
            WorkerId = ctx.AgentId,
        }, EventDirection.Self, ct);
    }

    private static int ResolveLlmTimeoutMs(StepRequestEvent request)
    {
        if (request.Parameters.TryGetValue("llm_timeout_ms", out var llmTimeoutRaw) &&
            int.TryParse(llmTimeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var llmTimeoutMs) &&
            llmTimeoutMs > 0)
        {
            return llmTimeoutMs;
        }

        if (request.Parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return DefaultLlmTimeoutMs;
    }

    private static bool TryExtractLlmFailure(string? content, out string error)
    {
        if (string.IsNullOrEmpty(content) ||
            !content.StartsWith(LlmFailureContentPrefix, StringComparison.Ordinal))
        {
            error = string.Empty;
            return false;
        }

        var extracted = content[LlmFailureContentPrefix.Length..].Trim();
        error = string.IsNullOrWhiteSpace(extracted) ? "LLM call failed." : extracted;
        return true;
    }

    private async Task HandleWatchdogTimeoutFiredAsync(
        LlmCallWatchdogTimeoutFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(evt.SessionId, out var pending))
            return;

        if (pending.WatchdogLease == null ||
            !WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.WatchdogLease))
        {
            ctx.Logger.LogDebug(
                "LLMCallModule: ignore watchdog without matching lease session={SessionId}",
                evt.SessionId);
            return;
        }

        runtimeState.PendingBySessionId.Remove(evt.SessionId);
        runtimeState.AttemptsByStepId.Remove(pending.StepId);
        await SaveStateAsync(runtimeState, ctx, ct);

        ctx.Logger.LogWarning(
            "LLMCallModule: step={StepId} timeout after {Timeout}ms (run={RunId}).",
            pending.StepId,
            evt.TimeoutMs,
            pending.RunId);

        try
        {
            await PublishFailedCompletionAsync(
                pending,
                $"LLM call timed out after {evt.TimeoutMs}ms",
                pending.TargetRole,
                ctx,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "LLMCallModule: failed to publish timeout completion for step={StepId}.",
                pending.StepId);
        }
    }

    private async Task FailPendingAsync(
        string sessionId,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        runtimeState.AttemptsByStepId.Remove(pending.StepId);
        await SaveStateAsync(runtimeState, ctx, ct);

        await PublishFailedCompletionAsync(pending, error, workerId, ctx, ct);
    }

    private static Task PublishFailedCompletionAsync(
        PendingLlmCallState pending,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = false,
                Error = error,
                WorkerId = string.IsNullOrWhiteSpace(workerId) ? ctx.AgentId : workerId,
            },
            EventDirection.Self,
            ct);

    private static string BuildWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(LlmWatchdogCallbackPrefix, sessionId);

    private async Task StopWatchdogAsync(
        PendingLlmCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.WatchdogLease == null)
            return;

        try
        {
            await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, pending.WatchdogLease, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(
                ex,
                "LLMCallModule: failed to cancel watchdog callback={CallbackId} generation={Generation}",
                pending.WatchdogLease.CallbackId,
                pending.WatchdogLease.Generation);
        }
    }

    private static Task SaveStateAsync(
        LLMCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingBySessionId.Count == 0 && state.AttemptsByStepId.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
