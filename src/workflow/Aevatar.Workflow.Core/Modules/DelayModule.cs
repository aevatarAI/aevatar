using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Timed delay module. Pauses for a configurable duration before completing.
/// Useful for rate limiting and spacing between API calls.
/// </summary>
public sealed class DelayModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "delay";

    public string Name => "delay";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(DelayStepTimeoutFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "delay")
                return;

            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var stepId = request.StepId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = false,
                    Error = "delay step requires non-empty run_id and step_id",
                }, EventDirection.Self, ct);
                return;
            }

            var durationMs = WorkflowParameterValueParser.GetBoundedInt(
                request.Parameters,
                1000,
                0,
                300_000,
                "duration_ms",
                "duration",
                "delay_ms");

            var pendingKey = new DelayPendingKey(runId, stepId);
            var state = WorkflowExecutionStateAccess.Load<DelayModuleState>(ctx, ModuleStateKey);
            await CancelPendingAsync(state, pendingKey, ctx, CancellationToken.None);

            ctx.Logger.LogInformation(
                "Delay {StepId}: schedule runtime callback after {Ms}ms (run={RunId})",
                stepId,
                durationMs,
                runId);

            if (durationMs <= 0)
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = true,
                    Output = request.Input ?? string.Empty,
                }, EventDirection.Self, ct);
                return;
            }

            state.Pending[BuildPendingKey(pendingKey)] = new PendingDelayState
            {
                Input = request.Input ?? string.Empty,
                CallbackId = BuildDelayCallbackId(runId, stepId, ResolveOriginEnvelopeId(envelope)),
            };
            await SaveStateAsync(state, ctx, ct);

            var pendingState = state.Pending[BuildPendingKey(pendingKey)];
            var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                pendingState.CallbackId,
                TimeSpan.FromMilliseconds(durationMs),
                new DelayStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    DurationMs = durationMs,
                },
                ct: ct);

            pendingState.Lease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            state.Pending[BuildPendingKey(pendingKey)] = pendingState;
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        if (!payload.Is(DelayStepTimeoutFiredEvent.Descriptor))
            return;

        var fired = payload.Unpack<DelayStepTimeoutFiredEvent>();
        var runIdFired = WorkflowRunIdNormalizer.Normalize(fired.RunId);
        var stepIdFired = fired.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runIdFired) || string.IsNullOrWhiteSpace(stepIdFired))
            return;

        var firedKey = new DelayPendingKey(runIdFired, stepIdFired);
        var stateForCallback = WorkflowExecutionStateAccess.Load<DelayModuleState>(ctx, ModuleStateKey);
        if (!stateForCallback.Pending.TryGetValue(BuildPendingKey(firedKey), out var pending))
            return;

        if (!MatchesPendingCallback(envelope, pending))
        {
            ctx.Logger.LogDebug(
                "Delay {StepId}: ignore callback without matching lease metadata run={RunId}",
                stepIdFired,
                runIdFired);
            return;
        }

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = stepIdFired,
            RunId = runIdFired,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);

        stateForCallback.Pending.Remove(BuildPendingKey(firedKey));
        await SaveStateAsync(stateForCallback, ctx, ct);
    }

    private async Task CancelPendingAsync(
        DelayModuleState state,
        DelayPendingKey key,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.Pending.Remove(BuildPendingKey(key), out var pending))
            return;

        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, pending.Lease, ct);
    }

    private static bool MatchesPendingCallback(EventEnvelope envelope, PendingDelayState pending)
    {
        if (pending.Lease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.Lease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.CallbackId, StringComparison.Ordinal);
    }

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id;

    private static string BuildDelayCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("delay-step", runId, stepId, originEnvelopeId);

    private readonly record struct DelayPendingKey(string RunId, string StepId);

    private static string BuildPendingKey(DelayPendingKey key) =>
        $"{key.RunId}:{key.StepId}";

    private static Task SaveStateAsync(
        DelayModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
