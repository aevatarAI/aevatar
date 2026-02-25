using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Pauses workflow execution until an external signal arrives.
/// On <c>StepRequestEvent(type=wait_signal)</c>, publishes <c>WaitingForSignalEvent</c> and suspends.
/// On <c>SignalReceivedEvent</c> matching the expected signal name, resumes by publishing <c>StepCompletedEvent</c>.
/// </summary>
public sealed class WaitSignalModule : IEventModule
{
    private readonly Dictionary<string, PendingSignal> _pending = new(StringComparer.OrdinalIgnoreCase);

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

            var signalName = request.Parameters.GetValueOrDefault("signal_name", "default");
            var prompt = request.Parameters.GetValueOrDefault("prompt", "");
            var timeoutMs = int.TryParse(request.Parameters.GetValueOrDefault("timeout_ms", "0"), out var t) ? t : 0;

            _pending[signalName] = new PendingSignal(request.StepId, request.RunId, request.Input ?? "");

            ctx.Logger.LogInformation("WaitSignal: step={StepId} waiting for signal={Signal}", request.StepId, signalName);

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
                        if (!_pending.ContainsKey(signalName)) return;
                        _pending.Remove(signalName);
                        ctx.Logger.LogWarning("WaitSignal: step={StepId} signal={Signal} timed out", stepId, signalName);
                        await ctx.PublishAsync(new StepCompletedEvent
                        {
                            StepId = stepId,
                            RunId = request.RunId,
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
            if (!_pending.Remove(signal.SignalName, out var pending)) return;

            ctx.Logger.LogInformation("WaitSignal: step={StepId} signal={Signal} received", pending.StepId, signal.SignalName);

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

    private sealed record PendingSignal(string StepId, string RunId, string Input);
}
