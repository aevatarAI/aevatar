using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Timed delay module. Pauses for a configurable duration before completing.
/// Useful for rate limiting and spacing between API calls.
/// </summary>
public sealed class DelayModule : IEventModule
{
    public string Name => "delay";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "delay") return;

        var durationMs = int.TryParse(request.Parameters.GetValueOrDefault("duration_ms", "1000"), out var d) ? d : 1000;
        durationMs = Math.Clamp(durationMs, 0, 300_000);

        ctx.Logger.LogInformation("Delay {StepId}: waiting {Ms}ms", request.StepId, durationMs);

        if (durationMs > 0)
            await Task.Delay(durationMs, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId, Success = true, Output = request.Input ?? "",
        }, EventDirection.Self, ct);
    }
}
