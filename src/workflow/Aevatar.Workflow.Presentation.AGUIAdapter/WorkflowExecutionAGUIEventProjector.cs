using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

/// <summary>
/// Projects workflow execution envelopes to AG-UI live events as a workflow projector branch.
/// </summary>
public sealed class WorkflowExecutionAGUIEventProjector
    : ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>, WorkflowRunEvent>
{
    private readonly IEventEnvelopeToAGUIEventMapper _mapper;

    public WorkflowExecutionAGUIEventProjector(
        IEventEnvelopeToAGUIEventMapper mapper,
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub)
        : base(runEventStreamHub)
    {
        _mapper = mapper;
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<WorkflowRunEvent>> ResolveSessionEventEntries(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope)
    {
        // Keep streaming pinned to the projection session command id.
        // Resume/signal events may arrive without (or with a different) correlation id,
        // but they still belong to the same live run session.
        var commandId = string.IsNullOrWhiteSpace(context.CommandId)
            ? envelope.CorrelationId
            : context.CommandId;
        if (string.IsNullOrWhiteSpace(commandId))
            return EmptyEntries;

        IReadOnlyList<AGUIEvent> aguiEvents = _mapper.Map(envelope);
        if (aguiEvents.Count == 0)
            return EmptyEntries;

        var entries = new List<ProjectionSessionEventEntry<WorkflowRunEvent>>(aguiEvents.Count);
        foreach (var aguiEvent in aguiEvents)
        {
            var runEvent = AGUIEventToWorkflowRunEventMapper.Map(aguiEvent);
            entries.Add(new ProjectionSessionEventEntry<WorkflowRunEvent>(
                context.RootActorId,
                commandId,
                runEvent));
        }

        return entries;
    }
}
