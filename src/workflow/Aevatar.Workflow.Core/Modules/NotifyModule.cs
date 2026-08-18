using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

public sealed class NotifyModule : IEventModule<IWorkflowExecutionContext>
{
    private const string AcceptedOutput = "accepted";

    public string Name => "notify";

    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(ctx);

        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, "notify", StringComparison.OrdinalIgnoreCase))
            return;

        var deliveryTargetId = ResolveDeliveryTargetId(request);
        var validationError = Validate(request, deliveryTargetId);
        if (validationError is not null)
        {
            await PublishFailureAsync(request, validationError, ctx, ct);
            return;
        }

        await ctx.PublishAsync(
            BuildNotificationEvent(request, deliveryTargetId!),
            TopologyAudience.ParentAndChildren,
            ct);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = AcceptedOutput,
            ExecutionId = request.ExecutionId,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        completed.Annotations["notification_status"] = AcceptedOutput;

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }

    private static string? ResolveDeliveryTargetId(StepRequestEvent request)
    {
        return Normalize(request.StepParameters?.DeliveryTargetId);
    }

    private static string? Validate(StepRequestEvent request, string? deliveryTargetId)
    {
        if (string.IsNullOrWhiteSpace(deliveryTargetId))
            return "notify requires explicit delivery_target_id.";

        var payloadCount = ResolvePayloadCount(request);
        return payloadCount switch
        {
            0 => "notify requires exactly one typed interaction payload.",
            1 => null,
            _ => "notify requires exactly one typed interaction payload; interaction_spec and interaction_template_spec are mutually exclusive.",
        };
    }

    private static int ResolvePayloadCount(StepRequestEvent request)
    {
        var count = 0;
        if (request.StepParameters?.InteractionSpec is { } interaction &&
            StepPresentation.HasInteractionSpec(interaction))
        {
            count++;
        }

        if (request.StepParameters?.InteractionTemplateSpec is { } template &&
            StepPresentation.HasInteractionTemplateSpec(template))
        {
            count++;
        }

        return count;
    }

    private static WorkflowInteractionNotificationEvent BuildNotificationEvent(
        StepRequestEvent request,
        string deliveryTargetId)
    {
        var notification = new WorkflowInteractionNotificationEvent
        {
            RunId = request.RunId,
            StepId = request.StepId,
            DeliveryTargetId = deliveryTargetId,
        };

        if (request.StepParameters?.InteractionSpec is { } interaction &&
            StepPresentation.HasInteractionSpec(interaction))
        {
            notification.Interaction = interaction.Clone();
        }
        else if (request.StepParameters?.InteractionTemplateSpec is { } template &&
                 StepPresentation.HasInteractionTemplateSpec(template))
        {
            notification.InteractionTemplate = template.Clone();
        }

        return notification;
    }

    private static async Task PublishFailureAsync(
        StepRequestEvent request,
        string error,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = error,
                ExecutionId = request.ExecutionId,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            },
            TopologyAudience.Self,
            ct);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
