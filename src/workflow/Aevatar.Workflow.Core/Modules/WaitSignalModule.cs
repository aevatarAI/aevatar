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
    private readonly Dictionary<(string RunId, string SignalName), PendingSignal> _pending = [];

    public string Name => "wait_signal";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var p = envelope.Payload;
        return p != null &&
               (p.Is(StepRequestEvent.Descriptor) || p.Is(SignalReceivedEvent.Descriptor));
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
            var pendingKey = (runId, signalName);

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
                            Error = $"signal '{signalName}' timed out after {timeoutMs}ms",
                        }, EventDirection.Self, CancellationToken.None);
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
            }
        }
        else if (payload.Is(SignalReceivedEvent.Descriptor))
        {
            var signal = payload.Unpack<SignalReceivedEvent>();
            if (!TryResolvePending(signal, out var pendingKey, out var pending))
            {
                ctx.Logger.LogWarning(
                    "WaitSignal: signal={Signal} run={RunId} not matched to pending waiters",
                    signal.SignalName,
                    string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId);
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
        out (string RunId, string SignalName) pendingKey,
        out PendingSignal pending)
    {
        var signalName = NormalizeSignalName(signal.SignalName);
        if (!string.IsNullOrWhiteSpace(signal.RunId))
        {
            pendingKey = (WorkflowRunIdNormalizer.Normalize(signal.RunId), signalName);
            return _pending.TryGetValue(pendingKey, out pending!);
        }

        // Backward compatibility: old clients may not send run_id.
        // Only resolve automatically when exactly one run is waiting for this signal.
        var matchCount = 0;
        pendingKey = default;
        pending = default!;
        foreach (var entry in _pending)
        {
            if (!entry.Key.SignalName.Equals(signalName, StringComparison.Ordinal))
                continue;

            matchCount++;
            if (matchCount > 1)
                return false;

            pendingKey = entry.Key;
            pending = entry.Value;
        }

        return matchCount == 1;
    }

    private static string NormalizeSignalName(string signalName)
    {
        var normalized = string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim();
        return normalized.ToLowerInvariant();
    }

    private sealed record PendingSignal(string StepId, string RunId, string Input, string SignalName);
}
