using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Timed delay module. Pauses for a configurable duration before completing.
/// Useful for rate limiting and spacing between API calls.
/// </summary>
public sealed class DelayModule : IEventModule
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

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
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
            var state = WorkflowRunModuleStateAccess.Load<DelayModuleState>(ctx, ModuleStateKey);
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

            var callbackId = BuildDelayCallbackId(runId, stepId);
            var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromMilliseconds(durationMs),
                new DelayStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    DurationMs = durationMs,
                },
                ct: ct);

            state.Pending[BuildPendingKey(pendingKey)] = new PendingDelayState
            {
                Lease = lease,
                Input = request.Input ?? string.Empty,
            };
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
        var stateForCallback = WorkflowRunModuleStateAccess.Load<DelayModuleState>(ctx, ModuleStateKey);
        if (!stateForCallback.Pending.TryGetValue(BuildPendingKey(firedKey), out var pending))
            return;

        if (!RuntimeCallbackEnvelopeMetadataReader.MatchesLease(envelope, pending.Lease))
        {
            ctx.Logger.LogDebug(
                "Delay {StepId}: ignore callback without matching lease metadata run={RunId}",
                stepIdFired,
                runIdFired);
            return;
        }

        stateForCallback.Pending.Remove(BuildPendingKey(firedKey));
        await SaveStateAsync(stateForCallback, ctx, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = stepIdFired,
            RunId = runIdFired,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    private async Task CancelPendingAsync(
        DelayModuleState state,
        DelayPendingKey key,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (!state.Pending.Remove(BuildPendingKey(key), out var pending))
            return;

        await ctx.CancelDurableCallbackAsync(
            pending.Lease,
            ct);

        await SaveStateAsync(state, ctx, ct);
    }

    private static string BuildDelayCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("delay-step", runId, stepId);

    private readonly record struct DelayPendingKey(string RunId, string StepId);

    private static string BuildPendingKey(DelayPendingKey key) =>
        $"{key.RunId}:{key.StepId}";

    private static Task SaveStateAsync(
        DelayModuleState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowRunModuleStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowRunModuleStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    public sealed class DelayModuleState
    {
        public Dictionary<string, PendingDelayState> Pending { get; set; } = [];
    }

    public sealed class PendingDelayState
    {
        public RuntimeCallbackLease Lease { get; set; } = null!;
        public string Input { get; set; } = string.Empty;
    }
}
