using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Pauses workflow execution until an external signal arrives.
/// On <c>StepRequestEvent(type=wait_signal)</c>, publishes <c>WaitingForSignalEvent</c> and suspends.
/// On <c>SignalReceivedEvent</c> matching the expected signal name, resumes by publishing <c>StepCompletedEvent</c>.
/// </summary>
public sealed class WaitSignalModule : IEventModule
{
    private const string ModuleStateKey = "wait_signal";

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
            var stepId = NormalizeStepId(request.StepId);
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
            var pendingKey = new PendingSignalKey(runId, signalName, stepId);
            var state = WorkflowRunModuleStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);

            ctx.Logger.LogInformation(
                "WaitSignal: step={StepId} run={RunId} waiting for signal={Signal}",
                stepId,
                runId,
                signalName);

            await ctx.PublishAsync(new WaitingForSignalEvent
            {
                StepId = stepId,
                SignalName = signalName,
                Prompt = prompt,
                TimeoutMs = timeoutMs,
                RunId = runId,
            }, EventDirection.Both, ct);

            if (state.Pending.Remove(BuildPendingKey(pendingKey), out var existingPending) &&
                existingPending.TimeoutLease != null)
            {
                await ctx.CancelDurableCallbackAsync(existingPending.TimeoutLease, CancellationToken.None);
            }

            if (timeoutMs > 0)
            {
                var timeoutFiredEvent = new WaitSignalTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    SignalName = signalName,
                    TimeoutMs = Math.Clamp(timeoutMs, 100, 3_600_000),
                };
                var callbackId = BuildTimeoutCallbackId(runId, signalName, stepId);
                var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                    callbackId,
                    TimeSpan.FromMilliseconds(timeoutFiredEvent.TimeoutMs),
                    timeoutFiredEvent,
                    ct: ct);
                state.Pending[BuildPendingKey(pendingKey)] = new PendingSignalState
                {
                    StepId = stepId,
                    RunId = runId,
                    Input = request.Input ?? string.Empty,
                    SignalName = signalName,
                    TimeoutLease = lease,
                };
            }
            else
            {
                state.Pending[BuildPendingKey(pendingKey)] = new PendingSignalState
                {
                    StepId = stepId,
                    RunId = runId,
                    Input = request.Input ?? string.Empty,
                    SignalName = signalName,
                    TimeoutLease = null,
                };
            }

            await SaveStateAsync(state, ctx, ct);
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
            var state = WorkflowRunModuleStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);
            if (!state.Pending.TryGetValue(BuildPendingKey(pendingKey), out var pending))
                return;

            if (pending.TimeoutLease == null ||
                !RuntimeCallbackEnvelopeMetadataReader.MatchesLease(envelope, pending.TimeoutLease))
            {
                ctx.Logger.LogDebug(
                    "WaitSignal: ignore timeout without matching lease run={RunId} step={StepId} signal={Signal}",
                    runId,
                    stepId,
                    signalName);
                return;
            }

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
            state.Pending.Remove(BuildPendingKey(pendingKey));
            await SaveStateAsync(state, ctx, ct);
        }
        else if (payload.Is(SignalReceivedEvent.Descriptor))
        {
            var signal = payload.Unpack<SignalReceivedEvent>();
            var state = WorkflowRunModuleStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);
            if (!TryResolvePending(state, signal, out var pendingKey, out var pending))
            {
                ctx.Logger.LogWarning(
                    "WaitSignal: signal={Signal} run={RunId} step={StepId} not matched to pending waiters",
                    signal.SignalName,
                    string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId,
                    string.IsNullOrWhiteSpace(signal.StepId) ? "(missing)" : signal.StepId);
                return;
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
            state.Pending.Remove(BuildPendingKey(pendingKey));
            await SaveStateAsync(state, ctx, ct);

            if (pending.TimeoutLease != null)
            {
                try
                {
                    await ctx.CancelDurableCallbackAsync(pending.TimeoutLease, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogDebug(
                        ex,
                        "WaitSignal: failed to cancel timeout after signal completion run={RunId} step={StepId} signal={Signal}",
                        pending.RunId,
                        pending.StepId,
                        pending.SignalName);
                }
            }
        }
    }

    private bool TryResolvePending(
        WaitSignalModuleState state,
        SignalReceivedEvent signal,
        out PendingSignalKey pendingKey,
        out PendingSignalState pending)
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
            var candidates = state.Pending
                .Where(x => string.Equals(x.Value.RunId, runId, StringComparison.Ordinal) &&
                            string.Equals(x.Value.SignalName, signalName, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count != 1)
                return false;

            pendingKey = new PendingSignalKey(
                candidates[0].Value.RunId,
                candidates[0].Value.SignalName,
                candidates[0].Value.StepId);
            pending = candidates[0].Value;
            return true;
        }

        pendingKey = new PendingSignalKey(runId, signalName, signalStepId);
        if (!state.Pending.TryGetValue(BuildPendingKey(pendingKey), out var resolved) || resolved == null)
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
        RuntimeCallbackKeyComposer.BuildCallbackId("wait-signal-timeout", runId, signalName, stepId);

    private readonly record struct PendingSignalKey(string RunId, string SignalName, string StepId);

    private static string BuildPendingKey(PendingSignalKey key) =>
        $"{key.RunId}:{key.SignalName}:{key.StepId}";

    private static Task SaveStateAsync(
        WaitSignalModuleState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowRunModuleStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowRunModuleStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    public sealed class WaitSignalModuleState
    {
        public Dictionary<string, PendingSignalState> Pending { get; set; } = [];
    }

    public sealed class PendingSignalState
    {
        public string StepId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        public string SignalName { get; set; } = string.Empty;
        public RuntimeCallbackLease? TimeoutLease { get; set; }
    }
}
