using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Pauses workflow execution until an external signal arrives.
/// On <c>StepRequestEvent(type=wait_signal)</c>, publishes <c>WaitingForSignalEvent</c> and suspends.
/// On <c>SignalReceivedEvent</c> matching the expected signal name, resumes by publishing <c>StepCompletedEvent</c>.
/// </summary>
public sealed class WaitSignalModule : IEventModule
{
    private readonly Dictionary<PendingSignalKey, PendingSignal> _pending = [];

    public string Name => "wait_signal";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var p = envelope.Payload;
        return p != null &&
               (p.Is(StepRequestEvent.Descriptor) ||
                p.Is(SignalReceivedEvent.Descriptor) ||
                p.Is(WaitSignalTimeoutFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "wait_signal") return;

            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var signalName = NormalizeSignalName(
                WorkflowParameterValueParser.GetString(request.Parameters, "default", "signal_name", "signal"));
            var prompt = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "prompt", "message");

            var timeoutMs = WorkflowParameterValueParser.GetBoundedInt(
                request.Parameters,
                0,
                0,
                3_600_000,
                "timeout_ms");
            if (timeoutMs <= 0 &&
                WorkflowParameterValueParser.TryGetBoundedInt(
                    request.Parameters,
                    out var timeoutSeconds,
                    0,
                    3_600,
                    "timeout_seconds",
                    "timeout"))
            {
                timeoutMs = Math.Clamp(timeoutSeconds * 1000, 0, 3_600_000);
            }
            var pendingKey = new PendingSignalKey(runId, signalName, request.StepId);

            ctx.Logger.LogInformation(
                "WaitSignal: step={StepId} run={RunId} waiting for signal={Signal}",
                request.StepId,
                runId,
                signalName);

            await ctx.PublishAsync(new WaitingForSignalEvent
            {
                StepId = request.StepId,
                SignalName = signalName,
                Prompt = prompt,
                TimeoutMs = timeoutMs,
                RunId = runId,
            }, EventDirection.Both, ct);

            if (timeoutMs > 0)
            {
                var stepId = request.StepId;
                var timeoutFiredEvent = new WaitSignalTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    SignalName = signalName,
                    TimeoutMs = Math.Clamp(timeoutMs, 100, 3_600_000),
                };
                var callbackId = BuildTimeoutCallbackId(runId, signalName, stepId);
                var lease = await ctx.ScheduleSelfTimeoutAsync(
                    callbackId,
                    TimeSpan.FromMilliseconds(timeoutFiredEvent.TimeoutMs),
                    timeoutFiredEvent,
                    ct: ct);
                _pending[pendingKey] = new PendingSignal(
                    request.StepId,
                    runId,
                    request.Input ?? "",
                    signalName,
                    lease);
            }
            else
            {
                _pending[pendingKey] = new PendingSignal(
                    request.StepId,
                    runId,
                    request.Input ?? "",
                    signalName,
                    TimeoutLease: null);
            }
        }
        else if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            var timeout = payload.Unpack<WaitSignalTimeoutFiredEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(timeout.RunId);
            var stepId = NormalizeStepId(timeout.StepId);
            if (string.IsNullOrWhiteSpace(stepId))
                return;

            var signalName = NormalizeSignalName(timeout.SignalName);
            var pendingKey = new PendingSignalKey(runId, signalName, stepId);
            if (!_pending.TryGetValue(pendingKey, out var pending))
                return;

            if (TryReadGeneration(envelope, out var firedGeneration) &&
                firedGeneration != pending.TimeoutLease?.Generation)
            {
                ctx.Logger.LogDebug(
                    "WaitSignal: ignore stale timeout run={RunId} step={StepId} signal={Signal} fired_generation={FiredGeneration} expected_generation={ExpectedGeneration}",
                    runId,
                    stepId,
                    signalName,
                    firedGeneration,
                    pending.TimeoutLease?.Generation ?? 0);
                return;
            }

            _pending.Remove(pendingKey);
            ctx.Logger.LogWarning(
                "WaitSignal: step={StepId} run={RunId} signal={Signal} timed out",
                stepId,
                runId,
                signalName);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = $"signal '{signalName}' timed out after {timeout.TimeoutMs}ms",
            }, EventDirection.Self, ct);
        }
        else if (payload.Is(SignalReceivedEvent.Descriptor))
        {
            var signal = payload.Unpack<SignalReceivedEvent>();
            if (!TryResolvePending(signal, out var pendingKey, out var pending))
            {
                ctx.Logger.LogWarning(
                    "WaitSignal: signal={Signal} run={RunId} step={StepId} not matched to pending waiters",
                    signal.SignalName,
                    string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId,
                    string.IsNullOrWhiteSpace(signal.StepId) ? "(missing)" : signal.StepId);
                return;
            }

            _pending.Remove(pendingKey);
            if (pending.TimeoutLease != null)
            {
                await ctx.CancelScheduledCallbackAsync(pending.TimeoutLease, CancellationToken.None);
            }

            ctx.Logger.LogInformation(
                "WaitSignal: step={StepId} run={RunId} signal={Signal} received",
                pending.StepId,
                pending.RunId,
                pending.SignalName);

            var output = string.IsNullOrEmpty(signal.Payload) ? pending.Input : signal.Payload;
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = true,
                Output = output,
            }, EventDirection.Self, ct);
        }
    }

    private bool TryResolvePending(
        SignalReceivedEvent signal,
        out PendingSignalKey pendingKey,
        out PendingSignal pending)
    {
        pendingKey = default;
        pending = default!;
        var signalName = NormalizeSignalName(signal.SignalName);
        if (string.IsNullOrWhiteSpace(signal.RunId))
            return false;

        var runId = WorkflowRunIdNormalizer.Normalize(signal.RunId);
        var signalStepId = NormalizeStepId(signal.StepId);
        if (string.IsNullOrWhiteSpace(signalStepId))
        {
            var candidates = _pending
                .Where(x => string.Equals(x.Key.RunId, runId, StringComparison.Ordinal) &&
                            string.Equals(x.Key.SignalName, signalName, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count != 1)
                return false;

            pendingKey = candidates[0].Key;
            pending = candidates[0].Value;
            return true;
        }

        pendingKey = new PendingSignalKey(runId, signalName, signalStepId);
        if (!_pending.TryGetValue(pendingKey, out var resolved) || resolved == null)
            return false;

        pending = resolved;
        return true;
    }

    private static string NormalizeSignalName(string signalName)
    {
        var normalized = string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim();
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeStepId(string? stepId) =>
        string.IsNullOrWhiteSpace(stepId) ? string.Empty : stepId.Trim();

    private static string BuildTimeoutCallbackId(string runId, string signalName, string stepId) =>
        string.Concat("wait-signal-timeout:", runId, ":", signalName, ":", stepId);

    private static bool TryReadGeneration(EventEnvelope envelope, out long generation)
    {
        generation = 0;
        return envelope.Metadata.TryGetValue(RuntimeCallbackMetadataKeys.CallbackGeneration, out var raw) &&
               long.TryParse(raw, out generation);
    }

    private readonly record struct PendingSignalKey(string RunId, string SignalName, string StepId);
    private sealed record PendingSignal(
        string StepId,
        string RunId,
        string Input,
        string SignalName,
        RuntimeCallbackLease? TimeoutLease);
}
