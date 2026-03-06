// ─────────────────────────────────────────────────────────────
// LLMCallModule - LLM call step module.
//
// Receives StepRequestEvent (step_type=llm_call), converts to
// ChatRequestEvent and sends to the specific target RoleGAgent
// via point-to-point SendToAsync (not broadcast Down).
//
// After RoleGAgent completes (TextMessageEndEvent bubbles Up),
// captures it and converts to StepCompletedEvent.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends ChatRequestEvent to a specific RoleGAgent by ID.</summary>
public sealed class LLMCallModule : IEventModule
{
    private const int DefaultLlmTimeoutMs = 1_800_000;
    private const string LlmTimeoutMetadataKey = "aevatar.llm_timeout_ms";
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;

    private readonly ConcurrentDictionary<string, PendingLlmCall> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _attemptsByRunStep = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _watchdogs = new(StringComparer.Ordinal);

    public LLMCallModule(WorkflowStepTargetAgentResolver? targetAgentResolver = null)
    {
        _targetAgentResolver = targetAgentResolver;
    }

    public string Name => "llm_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor)
                || payload.Is(TextMessageEndEvent.Descriptor)
                || payload.Is(ChatResponseEvent.Descriptor));
    }

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            await HandleStepRequestAsync(request, ctx, ct);
            return;
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            await HandleTextMessageEndAsync(evt, envelope, ctx, ct);
            return;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            await HandleChatResponseAsync(evt, ctx, ct);
        }
    }

    private async Task HandleStepRequestAsync(
        StepRequestEvent request,
        IEventHandlerContext ctx,
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
        var stepRunKey = $"{runId}:{request.StepId}";
        var attempt = _attemptsByRunStep.AddOrUpdate(stepRunKey, 1, static (_, current) => current + 1);

        // Use run/step/attempt-scoped session id to avoid collisions across concurrent runs and retries.
        var chatSessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, runId, request.StepId, attempt);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var promptPreview = prompt.Length > 200 ? prompt[..200] + "..." : prompt;
        var chatEvt = new ChatRequestEvent { Prompt = prompt, SessionId = chatSessionId };
        CopyParametersToChatMetadata(request.Parameters, chatEvt.Metadata);
        chatEvt.Metadata[LlmTimeoutMetadataKey] = timeoutMs.ToString(CultureInfo.InvariantCulture);

        WorkflowStepTargetAgentResolution target;
        try
        {
            target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: target resolution failed for step={StepId}", request.StepId);
            await PublishFailedCompletionAsync(request, $"LLM target resolution failed: {ex.Message}", ctx.AgentId, ctx, ct);
            return;
        }

        _pending[chatSessionId] = new PendingLlmCall(request, stepRunKey, target.WorkerId);
        StartWatchdog(chatSessionId, timeoutMs, ctx);

        try
        {
            if (!target.UseSelf)
            {
                // Point-to-point: send ChatRequestEvent directly to the target role actor by ID
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → SendTo mode={Mode} actor={ActorId} timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    request.StepId, target.Mode, target.ActorId, timeoutMs, prompt.Length, promptPreview);

                await ctx.SendToAsync(target.ActorId, chatEvt, ct);
            }
            else
            {
                // No target role: publish Self for WorkflowGAgent's own LLM
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → Self timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    request.StepId, timeoutMs, prompt.Length, promptPreview);

                await ctx.PublishAsync(chatEvt, EventDirection.Self, ct);
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: dispatch failed for step={StepId}", request.StepId);
            await FailPendingAsync(chatSessionId, $"LLM dispatch failed: {ex.Message}", target.WorkerId, ctx, ct);
        }
    }

    private async Task HandleTextMessageEndAsync(
        TextMessageEndEvent evt,
        EventEnvelope envelope,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;
        if (!_pending.TryRemove(sessionId, out var pending))
            return;

        StopWatchdog(sessionId);
        _attemptsByRunStep.TryRemove(pending.StepRunKey, out _);

        var outputPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending.Request, error, envelope.PublisherId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed ({Len} chars): {Preview}",
            pending.Request.StepId, evt.Content?.Length ?? 0, outputPreview);

        var completed = new StepCompletedEvent
        {
            StepId = pending.Request.StepId,
            RunId = pending.Request.RunId,
            Success = true,
            Output = evt.Content ?? string.Empty,
            WorkerId = envelope.PublisherId,
        };
        CopyRequestMetadataToCompletionMetadata(pending.Request, completed.Metadata);

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }

    private async Task HandleChatResponseAsync(
        ChatResponseEvent evt,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;
        if (!_pending.TryRemove(sessionId, out var pending))
            return;

        StopWatchdog(sessionId);
        _attemptsByRunStep.TryRemove(pending.StepRunKey, out _);

        var nsPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending.Request, error, ctx.AgentId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed non-streaming ({Len} chars): {Preview}",
            pending.Request.StepId, evt.Content?.Length ?? 0, nsPreview);

        var completed = new StepCompletedEvent
        {
            StepId = pending.Request.StepId,
            RunId = pending.Request.RunId,
            Success = true,
            Output = evt.Content ?? string.Empty,
            WorkerId = ctx.AgentId,
        };
        CopyRequestMetadataToCompletionMetadata(pending.Request, completed.Metadata);

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
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

    private static void CopyRequestMetadataToCompletionMetadata(
        StepRequestEvent request,
        MapField<string, string> metadata)
    {
        CopyParameterIfPresent(request.Parameters, metadata, "operation", "llm.operation");
        CopyParameterIfPresent(request.Parameters, metadata, "connector", "llm.connector");
        CopyParameterIfPresent(request.Parameters, metadata, "agent_type", "llm.agent_type");
    }

    private static void CopyParameterIfPresent(
        MapField<string, string> parameters,
        MapField<string, string> metadata,
        string parameterKey,
        string metadataKey)
    {
        if (!parameters.TryGetValue(parameterKey, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        metadata[metadataKey] = value.Trim();
    }

    private WorkflowStepTargetAgentResolver ResolveTargetAgentResolver(IEventHandlerContext ctx)
    {
        if (_targetAgentResolver != null)
            return _targetAgentResolver;

        var resolver = ctx.Services.GetService(typeof(WorkflowStepTargetAgentResolver)) as WorkflowStepTargetAgentResolver;
        if (resolver != null)
            return resolver;

        throw new InvalidOperationException(
            $"{nameof(WorkflowStepTargetAgentResolver)} is not registered in DI and was not provided to {nameof(LLMCallModule)}.");
    }

    private void StartWatchdog(string sessionId, int timeoutMs, IEventHandlerContext ctx)
    {
        if (timeoutMs <= 0)
            return;

        StopWatchdog(sessionId);
        var cts = new CancellationTokenSource();
        if (!_watchdogs.TryAdd(sessionId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = WatchdogAsync(sessionId, timeoutMs, ctx, cts.Token);
    }

    private async Task WatchdogAsync(string sessionId, int timeoutMs, IEventHandlerContext ctx, CancellationToken ct)
    {
        try
        {
            await Task.Delay(timeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_pending.TryRemove(sessionId, out var pending))
            return;

        StopWatchdog(sessionId);
        _attemptsByRunStep.TryRemove(pending.StepRunKey, out _);

        var request = pending.Request;
        ctx.Logger.LogWarning(
            "LLMCallModule: step={StepId} timeout after {Timeout}ms (run={RunId}).",
            request.StepId,
            timeoutMs,
            request.RunId);

        try
        {
            await PublishFailedCompletionAsync(
                request,
                $"LLM call timed out after {timeoutMs}ms",
                pending.WorkerId,
                ctx,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "LLMCallModule: failed to publish timeout completion for step={StepId}.",
                request.StepId);
        }
    }

    private async Task FailPendingAsync(
        string sessionId,
        string error,
        string workerId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (!_pending.TryRemove(sessionId, out var pending))
            return;

        StopWatchdog(sessionId);
        _attemptsByRunStep.TryRemove(pending.StepRunKey, out _);

        await PublishFailedCompletionAsync(pending.Request, error, workerId, ctx, ct);
    }

    private static Task PublishFailedCompletionAsync(
        StepRequestEvent pending,
        string error,
        string workerId,
        IEventHandlerContext ctx,
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

    private void StopWatchdog(string sessionId)
    {
        if (!_watchdogs.TryRemove(sessionId, out var cts))
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // best effort cancellation
        }
        finally
        {
            cts.Dispose();
        }
    }
    private sealed record PendingLlmCall(StepRequestEvent Request, string StepRunKey, string WorkerId);
}
