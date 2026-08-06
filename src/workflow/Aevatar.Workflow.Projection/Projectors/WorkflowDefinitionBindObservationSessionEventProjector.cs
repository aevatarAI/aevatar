using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowDefinitionBindObservationSessionEventProjector
    : ProjectionSessionEventProjectorBase<WorkflowDefinitionBindObservationProjectionContext, EventEnvelope>
{
    public WorkflowDefinitionBindObservationSessionEventProjector(
        WorkflowDefinitionBindObservationSessionEventHub sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<EventEnvelope>> ResolveSessionEventEntries(
        WorkflowDefinitionBindObservationProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.SessionId) ||
            !string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal) ||
            !CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload?.Is(BindWorkflowDefinitionEvent.Descriptor) != true)
        {
            return EmptyEntries;
        }

        return
        [
            new ProjectionSessionEventEntry<EventEnvelope>(
                context.RootActorId,
                context.SessionId,
                envelope),
        ];
    }
}
