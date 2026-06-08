using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowInteractionNotificationProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext>
{
    private readonly IChannelInteractionNotificationPort _notificationPort;

    public WorkflowInteractionNotificationProjector(IChannelInteractionNotificationPort notificationPort)
    {
        _notificationPort = notificationPort ?? throw new ArgumentNullException(nameof(notificationPort));
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
            return;

        if (envelope.Payload?.Is(WorkflowInteractionNotificationEvent.Descriptor) != true)
            return;

        var evt = envelope.Payload.Unpack<WorkflowInteractionNotificationEvent>();
        if (string.IsNullOrWhiteSpace(evt.DeliveryTargetId))
            return;

        await _notificationPort.DeliverAsync(
            new ChannelInteractionNotificationRequest
            {
                ActorId = context.RootActorId,
                RunId = evt.RunId,
                StepId = evt.StepId,
                DeliveryTargetId = evt.DeliveryTargetId,
                InteractionSpec = evt.PayloadCase == WorkflowInteractionNotificationEvent.PayloadOneofCase.Interaction
                    ? evt.Interaction
                    : null,
                InteractionTemplateSpec = evt.PayloadCase == WorkflowInteractionNotificationEvent.PayloadOneofCase.InteractionTemplate
                    ? evt.InteractionTemplate
                    : null,
            },
            ct);
    }
}
