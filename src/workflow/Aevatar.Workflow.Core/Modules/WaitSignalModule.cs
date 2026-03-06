using Aevatar.Foundation.Abstractions;
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
    private readonly Dictionary<PendingSignalKey, BufferedSignal> _buffered = [];
    private const int DefaultSignalBufferRetentionMs = 600_000;
    private const int MaxSignalBufferRetentionMs = 3_600_000;

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
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PruneExpiredBufferedSignals(nowMs);
            if (TryConsumeBufferedSignal(pendingKey, nowMs, out var buffered))
            {
                ctx.Logger.LogInformation(
                    "WaitSignal: step={StepId} run={RunId} signal={Signal} consumed from buffered callback",
                    request.StepId,
                    runId,
                    signalName);
                var bufferedOutput = string.IsNullOrEmpty(buffered.Payload) ? request.Input ?? string.Empty : buffered.Payload;
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = true,
                    Output = bufferedOutput,
                }, EventDirection.Self, ct);
                return;
            }

            _pending[pendingKey] = new PendingSignal(request.StepId, runId, request.Input ?? "", signalName);

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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Math.Clamp(timeoutMs, 100, 3_600_000), ct);
                        await ctx.PublishAsync(new WaitSignalTimeoutFiredEvent
                        {
                            RunId = runId,
                            StepId = stepId,
                            SignalName = signalName,
                            TimeoutMs = Math.Clamp(timeoutMs, 100, 3_600_000),
                        }, EventDirection.Self, CancellationToken.None);
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
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
            if (!_pending.Remove(pendingKey, out _))
                return;

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
                if (TryBufferSignal(signal, out var bufferedEvent))
                {
                    await ctx.PublishAsync(bufferedEvent, EventDirection.Both, ct);
                    ctx.Logger.LogInformation(
                        "WaitSignal: signal={Signal} run={RunId} step={StepId} buffered for deferred waiter activation",
                        bufferedEvent.SignalName,
                        bufferedEvent.RunId,
                        bufferedEvent.StepId);
                }
                else
                {
                    ctx.Logger.LogWarning(
                        "WaitSignal: signal={Signal} run={RunId} step={StepId} not matched to pending waiters",
                        signal.SignalName,
                        string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId,
                        string.IsNullOrWhiteSpace(signal.StepId) ? "(missing)" : signal.StepId);
                }
                return;
            }

            _pending.Remove(pendingKey);

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
        var signalName = NormalizeSignalName(signal.SignalName);
        var signalStepId = NormalizeStepId(signal.StepId);
        if (!string.IsNullOrWhiteSpace(signal.RunId))
        {
            var runId = WorkflowRunIdNormalizer.Normalize(signal.RunId);
            if (!string.IsNullOrWhiteSpace(signalStepId))
            {
                pendingKey = new PendingSignalKey(runId, signalName, signalStepId);
                return _pending.TryGetValue(pendingKey, out pending!);
            }

            return TryResolveSinglePending(
                static (key, state) => key.RunId.Equals(state.RunId, StringComparison.Ordinal) &&
                                       key.SignalName.Equals(state.SignalName, StringComparison.Ordinal),
                (RunId: runId, SignalName: signalName),
                out pendingKey,
                out pending);
        }

        // Backward compatibility: old clients may not send run_id.
        // Only resolve automatically when exactly one waiter matches.
        if (!string.IsNullOrWhiteSpace(signalStepId))
        {
            return TryResolveSinglePending(
                static (key, state) => key.SignalName.Equals(state.SignalName, StringComparison.Ordinal) &&
                                       key.StepId.Equals(state.StepId, StringComparison.Ordinal),
                (SignalName: signalName, StepId: signalStepId),
                out pendingKey,
                out pending);
        }

        return TryResolveSinglePending(
            static (key, signal) => key.SignalName.Equals(signal, StringComparison.Ordinal),
            signalName,
            out pendingKey,
            out pending);
    }

    private bool TryResolveSinglePending<TState>(
        Func<PendingSignalKey, TState, bool> predicate,
        TState state,
        out PendingSignalKey pendingKey,
        out PendingSignal pending)
    {
        var found = false;
        pendingKey = default;
        pending = default!;
        foreach (var entry in _pending)
        {
            if (!predicate(entry.Key, state))
                continue;

            if (found)
                return false;

            pendingKey = entry.Key;
            pending = entry.Value;
            found = true;
        }

        return found;
    }

    private static string NormalizeSignalName(string signalName)
    {
        var normalized = string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim();
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeStepId(string? stepId) =>
        string.IsNullOrWhiteSpace(stepId) ? string.Empty : stepId.Trim();

    private void PruneExpiredBufferedSignals(long nowUnixTimeMs)
    {
        if (_buffered.Count == 0)
            return;

        List<PendingSignalKey>? expiredKeys = null;
        foreach (var entry in _buffered)
        {
            if (entry.Value.ExpiresAtUnixTimeMs > nowUnixTimeMs)
                continue;
            expiredKeys ??= [];
            expiredKeys.Add(entry.Key);
        }

        if (expiredKeys == null)
            return;
        foreach (var key in expiredKeys)
            _buffered.Remove(key);
    }

    private bool TryConsumeBufferedSignal(
        PendingSignalKey key,
        long nowUnixTimeMs,
        out BufferedSignal buffered)
    {
        if (!_buffered.TryGetValue(key, out buffered!))
            return false;

        if (buffered.ExpiresAtUnixTimeMs <= nowUnixTimeMs)
        {
            _buffered.Remove(key);
            return false;
        }

        _buffered.Remove(key);
        return true;
    }

    private bool TryBufferSignal(SignalReceivedEvent signal, out WorkflowSignalBufferedEvent bufferedEvent)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(signal.RunId);
        var stepId = NormalizeStepId(signal.StepId);
        var signalName = NormalizeSignalName(signal.SignalName);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            bufferedEvent = new WorkflowSignalBufferedEvent();
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PruneExpiredBufferedSignals(nowMs);
        var key = new PendingSignalKey(runId, signalName, stepId);
        var payload = signal.Payload ?? string.Empty;
        _buffered[key] = new BufferedSignal(
            payload,
            nowMs,
            nowMs + Math.Clamp(DefaultSignalBufferRetentionMs, 1_000, MaxSignalBufferRetentionMs));
        bufferedEvent = new WorkflowSignalBufferedEvent
        {
            RunId = runId,
            StepId = stepId,
            SignalName = signalName,
            Payload = payload,
            ReceivedAtUnixTimeMs = nowMs,
        };
        return true;
    }

    private readonly record struct PendingSignalKey(string RunId, string SignalName, string StepId);
    private sealed record PendingSignal(string StepId, string RunId, string Input, string SignalName);
    private sealed record BufferedSignal(string Payload, long ReceivedAtUnixTimeMs, long ExpiresAtUnixTimeMs);
}
