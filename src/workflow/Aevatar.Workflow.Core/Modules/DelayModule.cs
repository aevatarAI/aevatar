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
public sealed class DelayModule : IEventModule
{
    private readonly Dictionary<DelayPendingKey, PendingDelay> _pending = [];

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
            await CancelPendingAsync(pendingKey, ctx, CancellationToken.None);

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
            var lease = await ctx.ScheduleSelfTimeoutAsync(
                callbackId,
                TimeSpan.FromMilliseconds(durationMs),
                new DelayStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = stepId,
                    DurationMs = durationMs,
                },
                ct: ct);

            _pending[pendingKey] = new PendingDelay(
                lease,
                request.Input ?? string.Empty);
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
        if (!_pending.TryGetValue(firedKey, out var pending))
            return;

        if (TryReadGeneration(envelope, out var firedGeneration) &&
            firedGeneration != pending.Lease.Generation)
        {
            ctx.Logger.LogDebug(
                "Delay {StepId}: ignore stale fired callback generation run={RunId} fired={FiredGeneration} expected={ExpectedGeneration}",
                stepIdFired,
                runIdFired,
                firedGeneration,
                pending.Lease.Generation);
            return;
        }

        _pending.Remove(firedKey);
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = stepIdFired,
            RunId = runIdFired,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    private async Task CancelPendingAsync(
        DelayPendingKey key,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (!_pending.Remove(key, out var pending))
            return;

        await ctx.CancelScheduledCallbackAsync(
            pending.Lease,
            ct);
    }

    private static bool TryReadGeneration(EventEnvelope envelope, out long generation)
    {
        generation = 0;
        return envelope.Metadata.TryGetValue(RuntimeCallbackMetadataKeys.CallbackGeneration, out var raw) &&
               long.TryParse(raw, out generation);
    }

    private static string BuildDelayCallbackId(string runId, string stepId) =>
        string.Concat("delay-step:", runId, ":", stepId);

    private readonly record struct DelayPendingKey(string RunId, string StepId);

    private sealed record PendingDelay(
        RuntimeCallbackLease Lease,
        string Input);
}
