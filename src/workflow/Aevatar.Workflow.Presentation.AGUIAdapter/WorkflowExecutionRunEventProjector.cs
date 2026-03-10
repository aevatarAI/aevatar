using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

/// <summary>
/// Projects workflow execution envelopes directly to workflow run event envelopes.
/// </summary>
public sealed class WorkflowExecutionRunEventProjector
    : ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>, WorkflowRunEventEnvelope>
{
    private readonly IEventEnvelopeToWorkflowRunEventMapper _mapper;

    public WorkflowExecutionRunEventProjector(
        IEventEnvelopeToWorkflowRunEventMapper mapper,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> runEventStreamHub)
        : base(runEventStreamHub)
    {
        _mapper = mapper;
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<WorkflowRunEventEnvelope>> ResolveSessionEventEntries(
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

        IReadOnlyList<WorkflowRunEventEnvelope> runEvents = _mapper.Map(envelope);
        if (runEvents.Count == 0)
            return EmptyEntries;

        var entries = new List<ProjectionSessionEventEntry<WorkflowRunEventEnvelope>>(runEvents.Count);
        foreach (var runEvent in runEvents)
        {
            entries.Add(new ProjectionSessionEventEntry<WorkflowRunEventEnvelope>(
                context.RootActorId,
                commandId,
                runEvent));
        }

        return entries;
    }
}
