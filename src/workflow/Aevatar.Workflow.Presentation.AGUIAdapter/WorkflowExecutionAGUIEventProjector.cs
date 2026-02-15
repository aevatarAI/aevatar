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

    public ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sink = context.GetAGUIEventSink();
        if (sink == null)
            return ValueTask.CompletedTask;

        IReadOnlyList<AGUIEvent> events = EventEnvelopeToAGUIEventMapper.Map(envelope);
        foreach (var aguiEvent in events)
            sink.Push(aguiEvent);

        return ValueTask.CompletedTask;
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
