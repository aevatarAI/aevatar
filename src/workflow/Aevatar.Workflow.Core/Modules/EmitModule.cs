using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Custom event emitter module. Publishes a <c>WorkflowCustomEvent</c> carrying
/// user-defined event type and payload for observability, inter-workflow signaling, or webhooks.
/// The step completes immediately after emitting.
/// </summary>
public sealed class EmitModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "emit";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "emit") return;

        var eventType = request.Parameters.GetValueOrDefault("event_type", "custom");
        var payload = request.Parameters.GetValueOrDefault("payload", request.Input ?? "");

        ctx.Logger.LogInformation("Emit {StepId}: type={EventType}", request.StepId, eventType);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = true,
            Output = request.Input ?? string.Empty,
            OutputProvenance = WorkflowStepOutputProvenance.ForwardedInput,
        };
        completed.Annotations["emit.event_type"] = eventType;
        completed.Annotations["emit.payload"] = payload;
        // The outward publication is the authored emit side effect. The exact
        // completion must also return to this run actor so its execution kernel
        // can advance; topology parent/child routing deliberately excludes the
        // publishing actor itself.
        await ctx.PublishAsync(completed.Clone(), TopologyAudience.ParentAndChildren, ct);
        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }
}
