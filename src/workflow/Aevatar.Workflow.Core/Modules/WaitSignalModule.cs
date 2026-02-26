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
            var signalName = NormalizeSignalName(request.Parameters.GetValueOrDefault("signal_name", "default"));
            var prompt = request.Parameters.GetValueOrDefault("prompt", "");
            var timeoutMs = int.TryParse(request.Parameters.GetValueOrDefault("timeout_ms", "0"), out var t) ? t : 0;
            var pendingKey = new PendingSignalKey(runId, signalName, request.StepId);

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
                ctx.Logger.LogWarning(
                    "WaitSignal: signal={Signal} run={RunId} step={StepId} not matched to pending waiters",
                    signal.SignalName,
                    string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId,
                    string.IsNullOrWhiteSpace(signal.StepId) ? "(missing)" : signal.StepId);
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

    private readonly record struct PendingSignalKey(string RunId, string SignalName, string StepId);
    private sealed record PendingSignal(string StepId, string RunId, string Input, string SignalName);
}
