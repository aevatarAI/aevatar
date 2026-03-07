using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>
/// Custom event emitter module. Publishes a <c>WorkflowCustomEvent</c> carrying
/// user-defined event type and payload for observability, inter-workflow signaling, or webhooks.
/// The step completes immediately after emitting.
/// </summary>
public sealed class EmitPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "emit";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "emit") return;

        var eventType = request.Parameters.GetValueOrDefault("event_type", "custom");
        var payload = request.Parameters.GetValueOrDefault("payload", request.Input ?? "");

        ctx.Logger.LogInformation("Emit {StepId}: type={EventType}", request.StepId, eventType);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId, Success = true, Output = request.Input ?? "",
        };
        completed.Metadata["emit.event_type"] = eventType;
        completed.Metadata["emit.payload"] = payload;
        await ctx.PublishAsync(completed, EventDirection.Both, ct);
    }
}
