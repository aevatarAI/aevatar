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
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends ChatRequestEvent to a specific RoleGAgent by ID.</summary>
public sealed class LLMCallModule : IEventModule
{
    private const int DefaultLlmTimeoutMs = 1_800_000;
    private const string LlmTimeoutMetadataKey = "aevatar.llm_timeout_ms";
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string LlmWatchdogCallbackPrefix = "llm-watchdog";

    private readonly Dictionary<string, PendingLlmCall> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attemptsByRunStep = new(StringComparer.Ordinal);

    public string Name => "llm_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor)
                || payload.Is(TextMessageEndEvent.Descriptor)
                || payload.Is(ChatResponseEvent.Descriptor)
                || payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor));
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
            return;
        }

        if (payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>();
            await HandleWatchdogTimeoutFiredAsync(evt, envelope, ctx, ct);
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
        var attempt = _attemptsByRunStep.GetValueOrDefault(stepRunKey, 0) + 1;
        _attemptsByRunStep[stepRunKey] = attempt;

        // Use run/step/attempt-scoped session id to avoid collisions across concurrent runs and retries.
        var chatSessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, runId, request.StepId, attempt);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var watchdogCallbackId = BuildWatchdogCallbackId(chatSessionId);
        _pending[chatSessionId] = new PendingLlmCall(
            request,
            stepRunKey,
            WatchdogLease: null);

        var targetRole = request.TargetRole;
        var promptPreview = prompt.Length > 200 ? prompt[..200] + "..." : prompt;
        var chatEvt = new ChatRequestEvent { Prompt = prompt, SessionId = chatSessionId };
        chatEvt.Metadata[LlmTimeoutMetadataKey] = timeoutMs.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (!string.IsNullOrEmpty(targetRole))
            {
                var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, targetRole);

                // Point-to-point: send ChatRequestEvent directly to the target role actor by ID
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → SendTo role={Role} actor={ActorId} timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    request.StepId, targetRole, targetActorId, timeoutMs, prompt.Length, promptPreview);

                await ctx.SendToAsync(targetActorId, chatEvt, ct);
            }
            else
            {
                // No target role: publish Self for WorkflowGAgent's own LLM
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → Self (no role) timeout={Timeout}ms prompt=({Len} chars) {Preview}",
                    request.StepId, timeoutMs, prompt.Length, promptPreview);

                await ctx.PublishAsync(chatEvt, EventDirection.Self, ct);
            }

            var lease = await ctx.ScheduleSelfTimeoutAsync(
                watchdogCallbackId,
                TimeSpan.FromMilliseconds(timeoutMs),
                new LlmCallWatchdogTimeoutFiredEvent
                {
                    SessionId = chatSessionId,
                    TimeoutMs = timeoutMs,
                    RunId = runId,
                    StepId = request.StepId,
                },
                ct: ct);
            _pending[chatSessionId] = _pending[chatSessionId] with { WatchdogLease = lease };
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: dispatch failed for step={StepId}", request.StepId);
            await FailPendingAsync(chatSessionId, $"LLM dispatch failed: {ex.Message}", ctx.AgentId, ctx, ct);
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
        if (!_pending.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        _attemptsByRunStep.Remove(pending.StepRunKey);

        var outputPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending.Request, error, envelope.PublisherId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed ({Len} chars): {Preview}",
            pending.Request.StepId, evt.Content?.Length ?? 0, outputPreview);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.Request.StepId,
            RunId = pending.Request.RunId,
            Success = true,
            Output = evt.Content ?? string.Empty,
            WorkerId = envelope.PublisherId,
        }, EventDirection.Self, ct);
    }

    private async Task HandleChatResponseAsync(
        ChatResponseEvent evt,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;
        if (!_pending.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        _attemptsByRunStep.Remove(pending.StepRunKey);

        var nsPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
        if (TryExtractLlmFailure(evt.Content, out var error))
        {
            await PublishFailedCompletionAsync(pending.Request, error, ctx.AgentId, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: step={StepId} completed non-streaming ({Len} chars): {Preview}",
            pending.Request.StepId, evt.Content?.Length ?? 0, nsPreview);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.Request.StepId,
            RunId = pending.Request.RunId,
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
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return;

        if (!_pending.TryGetValue(evt.SessionId, out var pending))
            return;

        if (TryReadGeneration(envelope, out var firedGeneration) &&
            firedGeneration != pending.WatchdogLease?.Generation)
        {
            ctx.Logger.LogDebug(
                "LLMCallModule: ignore stale watchdog session={SessionId} fired_generation={FiredGeneration} expected_generation={ExpectedGeneration}",
                evt.SessionId,
                firedGeneration,
                pending.WatchdogLease?.Generation ?? 0);
            return;
        }

        _pending.Remove(evt.SessionId);
        _attemptsByRunStep.Remove(pending.StepRunKey);

        var request = pending.Request;
        ctx.Logger.LogWarning(
            "LLMCallModule: step={StepId} timeout after {Timeout}ms (run={RunId}).",
            request.StepId,
            evt.TimeoutMs,
            request.RunId);

        try
        {
            await PublishFailedCompletionAsync(
                request,
                $"LLM call timed out after {evt.TimeoutMs}ms",
                request.TargetRole,
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
        if (!_pending.Remove(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        _attemptsByRunStep.Remove(pending.StepRunKey);

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

    private static string BuildWatchdogCallbackId(string sessionId) =>
        string.Concat(LlmWatchdogCallbackPrefix, ":", sessionId);

    private async Task StopWatchdogAsync(
        PendingLlmCall pending,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (pending.WatchdogLease == null)
            return;

        try
        {
            await ctx.CancelScheduledCallbackAsync(pending.WatchdogLease, ct);
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

    private static bool TryReadGeneration(EventEnvelope envelope, out long generation)
    {
        generation = 0;
        return envelope.Metadata.TryGetValue(RuntimeCallbackMetadataKeys.CallbackGeneration, out var raw) &&
               long.TryParse(raw, out generation);
    }

    private sealed record PendingLlmCall(
        StepRequestEvent Request,
        string StepRunKey,
        RuntimeCallbackLease? WatchdogLease);
}
