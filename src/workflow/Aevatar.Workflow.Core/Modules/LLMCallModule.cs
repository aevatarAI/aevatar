using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends <see cref="ChatRequestEvent"/> to a role actor or direct agent target.</summary>
public sealed class LLMCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const int DefaultLlmTimeoutMs = 1_800_000;
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string LlmWatchdogCallbackPrefix = "llm-watchdog";
    private const string ModuleStateKey = "llm_call";

    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;

    public LLMCallModule(WorkflowStepTargetAgentResolver? targetAgentResolver = null)
    {
        _targetAgentResolver = targetAgentResolver;
    }

    public string Name => "llm_call";
    public int Priority => 10;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(TextMessageEndEvent.Descriptor) ||
                payload.Is(ChatResponseEvent.Descriptor) ||
                payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor));
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

        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stepId))
        {
            await PublishFailedCompletionAsync(stepId, WorkflowRunIdNormalizer.Normalize(request.RunId), "llm_call step requires non-empty step_id", ctx.AgentId, ctx, ct);
            return;
        }

        var prompt = request.Input ?? string.Empty;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) &&
            !string.IsNullOrEmpty(prefix))
        {
            prompt = prefix.TrimEnd() + "\n\n" + prompt;
        }

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!TryResolvePending(runtimeState, runId, stepId, out var sessionId, out var pendingState))
        {
            var attemptKey = BuildAttemptKey(runId, stepId);
            var attempt = runtimeState.AttemptsByStepId.GetValueOrDefault(attemptKey, 0) + 1;
            runtimeState.AttemptsByStepId[attemptKey] = attempt;
            sessionId = CreateSessionId(ctx.AgentId, runId, stepId, attempt);
            pendingState = new PendingLlmCallState
            {
                StepId = stepId,
                RunId = runId,
                TargetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
                    workflow: null,
                    configuredTargetRole: request.TargetRole,
                    stepType: request.StepType,
                    parameters: request.Parameters),
                RequestDispatched = false,
                WatchdogCallbackId = BuildWatchdogCallbackId(sessionId),
                DispatchDedupId = BuildDispatchDedupId(sessionId),
            };
            runtimeState.PendingBySessionId[sessionId] = pendingState;
            await SaveStateAsync(runtimeState, ctx, ct);
        }

        await EnsureWatchdogScheduledAsync(sessionId, pendingState, timeoutMs, ctx, ct);
        pendingState = GetRequiredPending(sessionId, ctx);
        pendingState = await EnsureDispatchDedupIdAsync(sessionId, pendingState, ctx, ct);
        if (pendingState.RequestDispatched)
            return;

        WorkflowStepTargetAgentResolution target;
        try
        {
            target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: target resolution failed for step={StepId}", stepId);
            await FailPendingAsync(sessionId, $"LLM target resolution failed: {ex.Message}", ctx.AgentId, ctx, ct);
            return;
        }

        try
        {
            await DispatchChatRequestAsync(
                request,
                target,
                sessionId,
                pendingState.DispatchDedupId,
                prompt,
                timeoutMs,
                stepId,
                ctx,
                ct);
            await MarkRequestDispatchedAsync(sessionId, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: dispatch failed for step={StepId}", stepId);
            await FailPendingAsync(sessionId, $"LLM dispatch failed: {ex.Message}", target.WorkerId, ctx, ct);
        }
    }

    private async Task HandleTextMessageEndAsync(
        TextMessageEndEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        var publisherActorId = envelope.Route?.PublisherActorId ?? ctx.AgentId;
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending, error, publisherActorId, ctx, ct);
            await RemovePendingAsync(sessionId, pending, ctx, ct);
            return;
        }

        var outputPreview = (evt.Content ?? string.Empty).Length > 300
            ? evt.Content![..300] + "..."
            : evt.Content ?? string.Empty;
        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed ({Len} chars): {Preview}",
            pending.StepId,
            evt.Content?.Length ?? 0,
            outputPreview);

        await ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = true,
                Output = evt.Content ?? string.Empty,
                WorkerId = publisherActorId,
            },
            TopologyAudience.Self,
            ct);
        await RemovePendingAsync(sessionId, pending, ctx, ct);
    }

    private async Task HandleChatResponseAsync(
        ChatResponseEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending, error, ctx.AgentId, ctx, ct);
            await RemovePendingAsync(sessionId, pending, ctx, ct);
            return;
        }

        var outputPreview = (evt.Content ?? string.Empty).Length > 300
            ? evt.Content![..300] + "..."
            : evt.Content ?? string.Empty;
        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed non-streaming ({Len} chars): {Preview}",
            pending.StepId,
            evt.Content?.Length ?? 0,
            outputPreview);

        await ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = true,
                Output = evt.Content ?? string.Empty,
                WorkerId = ctx.AgentId,
            },
            TopologyAudience.Self,
            ct);
        await RemovePendingAsync(sessionId, pending, ctx, ct);
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

        if (!MatchesWatchdog(envelope, pending))
        {
            ctx.Logger.LogDebug(
                "LLMCallModule: ignore watchdog without matching lease session={SessionId}",
                evt.SessionId);
            return;
        }

        ctx.Logger.LogWarning(
            "LLMCallModule: step={StepId} timeout after {Timeout}ms (run={RunId}).",
            pending.StepId,
            evt.TimeoutMs,
            pending.RunId);

        await PublishFailedCompletionAsync(
            pending,
            $"LLM call timed out after {evt.TimeoutMs}ms",
            ctx.AgentId,
            ctx,
            ct);
        await RemovePendingAsync(evt.SessionId, pending, ctx, ct);
    }

    private async Task FailPendingAsync(
        string sessionId,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        await PublishFailedCompletionAsync(pending, error, workerId, ctx, ct);
        await RemovePendingAsync(sessionId, pending, ctx, ct);
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

    private static void CopyParametersToChatMetadata(
        MapField<string, string> parameters,
        MapField<string, string> metadata)
    {
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(key, "agent_type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "agent_id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metadata[key.Trim()] = value.Trim();
        }
    }

    private WorkflowStepTargetAgentResolver ResolveTargetAgentResolver(IEventContext ctx)
    {
        if (_targetAgentResolver != null)
            return _targetAgentResolver;

        var resolver = ctx.Services.GetService(typeof(WorkflowStepTargetAgentResolver)) as WorkflowStepTargetAgentResolver;
        if (resolver != null)
            return resolver;

        return new WorkflowStepTargetAgentResolver();
    }

    private async Task EnsureWatchdogScheduledAsync(
        string sessionId,
        PendingLlmCallState pending,
        int timeoutMs,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.WatchdogLease != null)
            return;

        var callbackId = string.IsNullOrWhiteSpace(pending.WatchdogCallbackId)
            ? BuildWatchdogCallbackId(sessionId)
            : pending.WatchdogCallbackId;
        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(timeoutMs),
            new LlmCallWatchdogTimeoutFiredEvent
            {
                SessionId = sessionId,
                TimeoutMs = timeoutMs,
                RunId = pending.RunId,
                StepId = pending.StepId,
            },
            ct: ct);

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var persistedPending))
            return;

        persistedPending.WatchdogCallbackId = callbackId;
        persistedPending.WatchdogLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        runtimeState.PendingBySessionId[sessionId] = persistedPending;
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private async Task DispatchChatRequestAsync(
        StepRequestEvent request,
        WorkflowStepTargetAgentResolution target,
        string sessionId,
        string dispatchDedupId,
        string prompt,
        int timeoutMs,
        string stepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var promptPreview = prompt.Length > 200 ? prompt[..200] + "..." : prompt;
        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
            TimeoutMs = timeoutMs,
        };
        CopyParametersToChatMetadata(request.Parameters, chatRequest.Metadata);
        WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(ctx, chatRequest.Metadata);
        var dispatchOptions = BuildDispatchOptions(dispatchDedupId);

        if (!target.UseSelf)
        {
            ctx.Logger.LogInformation(
                "LLMCallModule: step={StepId} → SendTo mode={Mode} actor={ActorId} timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                stepId,
                target.Mode,
                target.ActorId,
                timeoutMs,
                prompt.Length,
                promptPreview);
            await ctx.SendToAsync(target.ActorId, chatRequest, ct, dispatchOptions);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} → Self timeout={Timeout}ms prompt=({Len} chars) {Preview}",
            stepId,
            timeoutMs,
            prompt.Length,
            promptPreview);
        await ctx.PublishAsync(chatRequest, TopologyAudience.Self, ct, dispatchOptions);
    }

    private static Task PublishFailedCompletionAsync(
        PendingLlmCallState pending,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        PublishFailedCompletionAsync(
            pending.StepId,
            pending.RunId,
            error,
            workerId,
            ctx,
            ct);

    private static Task PublishFailedCompletionAsync(
        string stepId,
        string runId,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = error,
                WorkerId = string.IsNullOrWhiteSpace(workerId) ? ctx.AgentId : workerId,
            },
            TopologyAudience.Self,
            ct);

    private static string BuildWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(LlmWatchdogCallbackPrefix, sessionId);

    private static string BuildDispatchDedupId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-llm-dispatch", sessionId);

    private static string BuildAttemptKey(string runId, string stepId) =>
        string.IsNullOrWhiteSpace(runId) ? stepId : $"{runId}:{stepId}";

    private static string CreateSessionId(string scopeId, string runId, string stepId, int attempt) =>
        string.IsNullOrWhiteSpace(runId)
            ? ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, $"{stepId}:a{attempt}")
            : ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, runId, stepId, attempt);

    private static EventEnvelopePublishOptions BuildDispatchOptions(string dispatchDedupId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                DeduplicationOperationId = dispatchDedupId,
            },
        };

    private static bool TryResolvePending(
        LLMCallModuleState state,
        string runId,
        string stepId,
        out string sessionId,
        out PendingLlmCallState pending)
    {
        foreach (var entry in state.PendingBySessionId)
        {
            if (!string.Equals(entry.Value.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(entry.Value.StepId, stepId, StringComparison.Ordinal))
            {
                continue;
            }

            sessionId = entry.Key;
            pending = entry.Value;
            return true;
        }

        sessionId = string.Empty;
        pending = default!;
        return false;
    }

    private static PendingLlmCallState GetRequiredPending(string sessionId, IWorkflowExecutionContext ctx)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        return runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pendingState)
            ? pendingState
            : throw new InvalidOperationException($"Missing pending LLM call state for session {sessionId}.");
    }

    private static async Task MarkRequestDispatchedAsync(
        string sessionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pendingState))
            return;

        pendingState.RequestDispatched = true;
        runtimeState.PendingBySessionId[sessionId] = pendingState;
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private static async Task<PendingLlmCallState> EnsureDispatchDedupIdAsync(
        string sessionId,
        PendingLlmCallState pendingState,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pendingState.DispatchDedupId))
            return pendingState;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out pendingState))
            throw new InvalidOperationException($"Missing pending LLM call state for session {sessionId}.");

        pendingState.DispatchDedupId = BuildDispatchDedupId(sessionId);
        runtimeState.PendingBySessionId[sessionId] = pendingState;
        await SaveStateAsync(runtimeState, ctx, ct);
        return pendingState;
    }

    private static async Task RemovePendingAsync(
        string sessionId,
        PendingLlmCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.Remove(sessionId))
            return;

        runtimeState.AttemptsByStepId.Remove(BuildAttemptKey(pending.RunId, pending.StepId));
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private static bool MatchesWatchdog(EventEnvelope envelope, PendingLlmCallState pending)
    {
        if (pending.WatchdogLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.WatchdogLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.WatchdogCallbackId, StringComparison.Ordinal);
    }

    private static async Task StopWatchdogAsync(
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
