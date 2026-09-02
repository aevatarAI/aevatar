using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowHumanApprovalResolutionProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext>
{
    private readonly IHumanInteractionPort _humanInteractionPort;

    public WorkflowHumanApprovalResolutionProjector(IHumanInteractionPort humanInteractionPort)
    {
        _humanInteractionPort = humanInteractionPort ?? throw new ArgumentNullException(nameof(humanInteractionPort));
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

        var sourceEnvelope = CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observedEnvelope) &&
                             observedEnvelope?.Payload != null
            ? observedEnvelope
            : envelope;
        if (sourceEnvelope.Payload?.Is(WorkflowHumanApprovalResolvedEvent.Descriptor) != true)
            return;

        var evt = sourceEnvelope.Payload.Unpack<WorkflowHumanApprovalResolvedEvent>();
        if (string.IsNullOrWhiteSpace(evt.DeliveryTargetId))
            return;

        await _humanInteractionPort.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = context.RootActorId,
                RunId = evt.RunId,
                StepId = evt.StepId,
                SourceEventId = sourceEnvelope.Id,
                IssuedAtUnixMs = AGUIEventEnvelopeMappingHelpers.ToUnixMs(sourceEnvelope.Timestamp) ?? 0,
                Approved = evt.Approved,
                UserInput = string.IsNullOrWhiteSpace(evt.UserInput) ? null : evt.UserInput,
                EditedContent = string.IsNullOrWhiteSpace(evt.EditedContent) ? null : evt.EditedContent,
                Feedback = string.IsNullOrWhiteSpace(evt.Feedback) ? null : evt.Feedback,
                ResolvedContent = string.IsNullOrWhiteSpace(evt.ResolvedContent) ? null : evt.ResolvedContent,
                TimedOut = evt.ResolutionSource == WorkflowHumanApprovalResolutionSource.Timeout,
            },
            evt.DeliveryTargetId,
            ct);
    }
}
