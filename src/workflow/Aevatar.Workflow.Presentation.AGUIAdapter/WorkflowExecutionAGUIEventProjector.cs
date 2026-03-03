
using Aevatar.CQRS.Projection.Abstractions;
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
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IEventEnvelopeToAGUIEventMapper _mapper;
    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;

    public WorkflowExecutionAGUIEventProjector(
        IEventEnvelopeToAGUIEventMapper mapper,
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub)
    {
        _mapper = mapper;
        _runEventStreamHub = runEventStreamHub;
    }

    public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var commandId = envelope.CorrelationId;
        if (string.IsNullOrWhiteSpace(commandId))
            return;

        IReadOnlyList<AGUIEvent> aguiEvents = _mapper.Map(envelope);
        if (aguiEvents.Count == 0)
            return;

        foreach (var aguiEvent in aguiEvents)
        {
            var runEvent = AGUIEventToWorkflowRunEventMapper.Map(aguiEvent);
            await _runEventStreamHub.PublishAsync(context.RootActorId, commandId, runEvent, ct);
        }
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
