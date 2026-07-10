using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Pauses workflow execution until an external signal arrives.
/// On <c>StepRequestEvent(type=wait_signal)</c>, publishes <c>WaitingForSignalEvent</c> and suspends.
/// On <c>SignalReceivedEvent</c> matching the expected signal name, resumes by publishing <c>StepCompletedEvent</c>.
/// </summary>
public sealed class WaitSignalModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "wait_signal";
    private const int DefaultSignalBufferRetentionMs = 600_000;
    private const int MaxSignalBufferRetentionMs = 3_600_000;
    private const int MaxWaitSignalTimeoutMs = 86_400_000;
    private const int MaxWaitSignalTimeoutSeconds = MaxWaitSignalTimeoutMs / 1000;

    public string Name => "wait_signal";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(SignalReceivedEvent.Descriptor) ||
                payload.Is(WaitSignalTimeoutFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "wait_signal")
                return;

            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var stepId = NormalizeStepId(request.StepId);
            var signalName = NormalizeSignalName(
                !string.IsNullOrWhiteSpace(request.StepParameters?.ExternalApproval?.SignalName)
                    ? request.StepParameters.ExternalApproval.SignalName
                    : WorkflowParameterValueParser.GetString(request.Parameters, "default", "signal_name", "signal"));
            var prompt = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "prompt", "message");
            var timeoutMs = ResolveTimeoutMs(request.Parameters);
            var pendingKey = new PendingSignalKey(runId, signalName, stepId);
            var state = WorkflowExecutionStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);
            var externalApproval = ResolveExternalApproval(request);
            // Refactor (iter89/cluster-089-workflow-module-clock-state):
            //   Old: wait_signal used process wall clock for buffered signal
            //        eviction and received timestamps.
            //   New: wait_signal uses the workflow execution context clock for
            //        business-time buffer state.
            var nowMs = ctx.UtcNow.ToUnixTimeMilliseconds();
            PruneExpiredBufferedSignals(state, nowMs);

            if (TryConsumeBufferedSignal(state, pendingKey, nowMs, out var buffered))
            {
                await SaveStateAsync(state, ctx, ct);
                ctx.Logger.LogInformation(
                    "WaitSignal: step={StepId} run={RunId} signal={Signal} consumed from buffered callback",
                    stepId,
                    runId,
                    signalName);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = true,
                    Output = string.IsNullOrEmpty(buffered.Payload) ? request.Input ?? string.Empty : buffered.Payload,
                }, TopologyAudience.Self, ct);
                return;
            }

            ctx.Logger.LogInformation(
                "WaitSignal: step={StepId} run={RunId} waiting for signal={Signal}",
                stepId,
                runId,
                signalName);

            await CancelPendingAsync(state, pendingKey, ctx, CancellationToken.None);

            var pendingState = new PendingSignalState
            {
                StepId = stepId,
                RunId = runId,
                Input = request.Input ?? string.Empty,
                SignalName = signalName,
                TimeoutLease = null,
                TimeoutCallbackId = timeoutMs > 0
                    ? BuildTimeoutCallbackId(runId, signalName, stepId, ResolveOriginEnvelopeId(envelope))
                    : string.Empty,
                ExternalApproval = externalApproval,
            };
            state.Pending[BuildPendingKey(pendingKey)] = pendingState;
            await SaveStateAsync(state, ctx, ct);
            await PublishExternalApprovalRegisteredAsync(pendingState, ctx, ct);

            if (timeoutMs > 0)
            {
                var timeoutEvent = new WaitSignalTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    SignalName = signalName,
                    TimeoutMs = Math.Clamp(timeoutMs, 100, MaxWaitSignalTimeoutMs),
                };
                var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                    pendingState.TimeoutCallbackId,
                    TimeSpan.FromMilliseconds(timeoutEvent.TimeoutMs),
                    timeoutEvent,
                    ct: ct);
                pendingState.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
                state.Pending[BuildPendingKey(pendingKey)] = pendingState;
                await SaveStateAsync(state, ctx, ct);
            }

            await ctx.PublishAsync(new WaitingForSignalEvent
            {
                StepId = stepId,
                SignalName = signalName,
                Prompt = prompt,
                TimeoutMs = timeoutMs,
                RunId = runId,
            }, TopologyAudience.ParentAndChildren, ct);
            return;
        }

        if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            var timeout = payload.Unpack<WaitSignalTimeoutFiredEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(timeout.RunId);
            var stepId = NormalizeStepId(timeout.StepId);
            if (string.IsNullOrWhiteSpace(stepId))
                return;

            var signalName = NormalizeSignalName(timeout.SignalName);
            var pendingKey = new PendingSignalKey(runId, signalName, stepId);
            var state = WorkflowExecutionStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);
            if (!state.Pending.TryGetValue(BuildPendingKey(pendingKey), out var pending))
                return;

            if (!MatchesTimeout(envelope, pending))
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
            }, TopologyAudience.Self, ct);

            state.Pending.Remove(BuildPendingKey(pendingKey));
            await SaveStateAsync(state, ctx, ct);
            await PublishExternalApprovalClearedAsync(pending, ctx, ct);
            return;
        }

        var signal = payload.Unpack<SignalReceivedEvent>();
        var stateForSignal = WorkflowExecutionStateAccess.Load<WaitSignalModuleState>(ctx, ModuleStateKey);
        var signalNowMs = ctx.UtcNow.ToUnixTimeMilliseconds();
        PruneExpiredBufferedSignals(stateForSignal, signalNowMs);
        if (!TryResolvePending(stateForSignal, signal, out var resolvedKey, out var pendingStateForSignal))
        {
            if (TryBufferSignal(stateForSignal, signal, signalNowMs, out var bufferedEvent))
            {
                await SaveStateAsync(stateForSignal, ctx, ct);
                await ctx.PublishAsync(bufferedEvent, TopologyAudience.ParentAndChildren, ct);
                ctx.Logger.LogInformation(
                    "WaitSignal: signal={Signal} run={RunId} step={StepId} buffered for deferred waiter activation",
                    bufferedEvent.SignalName,
                    bufferedEvent.RunId,
                    bufferedEvent.StepId);
            }
            else
            {
                await SaveStateAsync(stateForSignal, ctx, ct);
                ctx.Logger.LogWarning(
                    "WaitSignal: signal={Signal} run={RunId} step={StepId} not matched to pending waiters",
                    signal.SignalName,
                    string.IsNullOrWhiteSpace(signal.RunId) ? "(missing)" : signal.RunId,
                    string.IsNullOrWhiteSpace(signal.StepId) ? "(missing)" : signal.StepId);
            }

            return;
        }

        ctx.Logger.LogInformation(
            "WaitSignal: step={StepId} run={RunId} signal={Signal} received",
            pendingStateForSignal.StepId,
            pendingStateForSignal.RunId,
            pendingStateForSignal.SignalName);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = pendingStateForSignal.StepId,
            RunId = pendingStateForSignal.RunId,
            Success = true,
            Output = string.IsNullOrEmpty(signal.Payload) ? pendingStateForSignal.Input : signal.Payload,
        }, TopologyAudience.Self, ct);

        stateForSignal.Pending.Remove(BuildPendingKey(resolvedKey));
        await SaveStateAsync(stateForSignal, ctx, ct);
        await PublishExternalApprovalClearedAsync(pendingStateForSignal, ctx, ct);

        if (pendingStateForSignal.TimeoutLease != null)
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pendingStateForSignal.TimeoutLease,
                $"WaitSignal timeout cleanup run={pendingStateForSignal.RunId} step={pendingStateForSignal.StepId} signal={pendingStateForSignal.SignalName}",
                CancellationToken.None);
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

    private static PendingExternalApprovalContinuationState? ResolveExternalApproval(
        StepRequestEvent request)
    {
        var options = request.StepParameters?.ExternalApproval;
        if (options == null)
            return null;

        var sourceId = NormalizeIdentity(options.SourceId);
        var externalIdKind = NormalizeIdentity(options.ExternalIdKind);
        var externalId = NormalizeIdentity(options.ExternalId);
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(externalIdKind) ||
            string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        return new PendingExternalApprovalContinuationState
        {
            SourceId = sourceId,
            ExternalIdKind = externalIdKind,
            ExternalId = externalId,
            CallbackIdempotencyKey = NormalizeIdentity(options.CallbackIdempotencyKey),
            RequestId = NormalizeIdentity(options.RequestId),
        };
    }

    private static async Task PublishExternalApprovalRegisteredAsync(
        PendingSignalState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var externalApproval = pending.ExternalApproval;
        if (externalApproval == null ||
            string.IsNullOrWhiteSpace(externalApproval.SourceId) ||
            string.IsNullOrWhiteSpace(externalApproval.ExternalIdKind) ||
            string.IsNullOrWhiteSpace(externalApproval.ExternalId))
        {
            return;
        }

        await ctx.PublishAsync(new WorkflowExternalApprovalContinuationRegisteredEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            SignalName = pending.SignalName,
            SourceId = externalApproval.SourceId,
            ExternalIdKind = externalApproval.ExternalIdKind,
            ExternalId = externalApproval.ExternalId,
            CallbackIdempotencyKey = externalApproval.CallbackIdempotencyKey,
            RequestId = externalApproval.RequestId,
        }, TopologyAudience.ParentAndChildren, ct);
    }

    private static async Task PublishExternalApprovalClearedAsync(
        PendingSignalState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var externalApproval = pending.ExternalApproval;
        if (externalApproval == null ||
            string.IsNullOrWhiteSpace(externalApproval.SourceId) ||
            string.IsNullOrWhiteSpace(externalApproval.ExternalIdKind) ||
            string.IsNullOrWhiteSpace(externalApproval.ExternalId))
        {
            return;
        }

        await ctx.PublishAsync(new WorkflowExternalApprovalContinuationClearedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            SignalName = pending.SignalName,
            SourceId = externalApproval.SourceId,
            ExternalIdKind = externalApproval.ExternalIdKind,
            ExternalId = externalApproval.ExternalId,
            CallbackIdempotencyKey = externalApproval.CallbackIdempotencyKey,
            RequestId = externalApproval.RequestId,
        }, TopologyAudience.ParentAndChildren, ct);
    }

    private static int ResolveTimeoutMs(IReadOnlyDictionary<string, string> parameters)
    {
        var timeoutMs = WorkflowParameterValueParser.GetBoundedInt(
            parameters,
            0,
            0,
            MaxWaitSignalTimeoutMs,
            "timeout_ms");
        if (timeoutMs > 0)
            return timeoutMs;

        if (WorkflowParameterValueParser.TryGetBoundedInt(
                parameters,
                out var timeoutSeconds,
                0,
                MaxWaitSignalTimeoutSeconds,
                "timeout_seconds",
                "timeout"))
        {
            return Math.Clamp(timeoutSeconds * 1000, 0, MaxWaitSignalTimeoutMs);
        }

        return 0;
    }

    private static string NormalizeSignalName(string signalName)
    {
        var normalized = string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim();
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeStepId(string? stepId) =>
        string.IsNullOrWhiteSpace(stepId) ? string.Empty : stepId.Trim();

    private static bool MatchesTimeout(EventEnvelope envelope, PendingSignalState pending)
    {
        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id;

    private static string BuildTimeoutCallbackId(string runId, string signalName, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("wait-signal-timeout", runId, signalName, stepId, originEnvelopeId);

    private readonly record struct PendingSignalKey(string RunId, string SignalName, string StepId);

    private static string BuildPendingKey(PendingSignalKey key) =>
        $"{key.RunId}:{key.SignalName}:{key.StepId}";

    private static async Task CancelPendingAsync(
        WaitSignalModuleState state,
        PendingSignalKey key,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.Pending.Remove(BuildPendingKey(key), out var existingPending))
            return;

        await SaveStateAsync(state, ctx, ct);
        await PublishExternalApprovalClearedAsync(existingPending, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            existingPending.TimeoutLease,
            $"WaitSignal replaced waiter cleanup run={existingPending.RunId} step={existingPending.StepId} signal={existingPending.SignalName}",
            ct);
    }

    private static Task SaveStateAsync(
        WaitSignalModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0 && state.Buffered.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private static void PruneExpiredBufferedSignals(WaitSignalModuleState state, long nowUnixTimeMs)
    {
        if (state.Buffered.Count == 0)
            return;

        var expiredKeys = state.Buffered
            .Where(entry => entry.Value.ExpiresAtUnixTimeMs <= nowUnixTimeMs)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var key in expiredKeys)
            state.Buffered.Remove(key);
    }

    private static bool TryConsumeBufferedSignal(
        WaitSignalModuleState state,
        PendingSignalKey key,
        long nowUnixTimeMs,
        out BufferedSignalState buffered)
    {
        buffered = default!;
        if (!state.Buffered.TryGetValue(BuildPendingKey(key), out var stored))
            return false;

        if (stored.ExpiresAtUnixTimeMs <= nowUnixTimeMs)
        {
            state.Buffered.Remove(BuildPendingKey(key));
            return false;
        }

        state.Buffered.Remove(BuildPendingKey(key));
        buffered = stored;
        return true;
    }

    private static bool TryBufferSignal(
        WaitSignalModuleState state,
        SignalReceivedEvent signal,
        long nowUnixTimeMs,
        out WorkflowSignalBufferedEvent bufferedEvent)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(signal.RunId);
        var stepId = NormalizeStepId(signal.StepId);
        var signalName = NormalizeSignalName(signal.SignalName);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            bufferedEvent = new WorkflowSignalBufferedEvent();
            return false;
        }

        var key = new PendingSignalKey(runId, signalName, stepId);
        var payload = signal.Payload ?? string.Empty;
        state.Buffered[BuildPendingKey(key)] = new BufferedSignalState
        {
            Payload = payload,
            ReceivedAtUnixTimeMs = nowUnixTimeMs,
            ExpiresAtUnixTimeMs = nowUnixTimeMs + Math.Clamp(DefaultSignalBufferRetentionMs, 1_000, MaxSignalBufferRetentionMs),
        };
        bufferedEvent = new WorkflowSignalBufferedEvent
        {
            RunId = runId,
            StepId = stepId,
            SignalName = signalName,
            Payload = payload,
            ReceivedAtUnixTimeMs = nowUnixTimeMs,
        };
        return true;
    }
}
