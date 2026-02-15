using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

/// <summary>
/// Projects workflow execution envelopes to AG-UI live events as a workflow projector branch.
/// </summary>
public sealed class WorkflowExecutionAGUIEventProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    public int Order => 100;

    public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sink = context.GetRunEventSink();
        if (sink == null)
            return;

        IReadOnlyList<AGUIEvent> aguiEvents = EventEnvelopeToAGUIEventMapper.Map(envelope);
        foreach (var aguiEvent in aguiEvents)
        {
            try
            {
                var runEvent = AGUIEventToWorkflowRunEventMapper.Map(aguiEvent);
                await sink.PushAsync(runEvent, ct);
            }
            catch (InvalidOperationException)
            {
                // Sink is completed/full in non-wait mode; do not fail the whole projection pipeline.
                context.DetachRunEventSink();
                break;
            }
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
