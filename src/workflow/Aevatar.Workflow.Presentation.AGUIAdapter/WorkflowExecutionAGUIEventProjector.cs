using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Workflow.Projection;
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

        var sink = context.GetAGUIEventSink();
        if (sink == null)
            return;

        IReadOnlyList<AGUIEvent> events = EventEnvelopeToAGUIEventMapper.Map(envelope);
        foreach (var aguiEvent in events)
        {
            try
            {
                await sink.PushAsync(aguiEvent, ct);
            }
            catch (InvalidOperationException)
            {
                // Sink is completed/full in non-wait mode; do not fail the whole projection pipeline.
                context.DetachAGUIEventSink();
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
