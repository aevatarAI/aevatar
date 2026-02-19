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

    public WorkflowExecutionAGUIEventProjector(IEventEnvelopeToAGUIEventMapper mapper)
    {
        _mapper = mapper;
    }

    public int Order => 100;

    public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sinks = context.GetLiveSinksSnapshot(envelope.CorrelationId);
        if (sinks.Count == 0)
            return;

        IReadOnlyList<AGUIEvent> aguiEvents = _mapper.Map(envelope);
        foreach (var aguiEvent in aguiEvents)
        {
            var runEvent = AGUIEventToWorkflowRunEventMapper.Map(aguiEvent);
            foreach (var sink in sinks)
            {
                try
                {
                    await sink.PushAsync(runEvent, ct);
                }
                catch (WorkflowRunEventSinkBackpressureException)
                {
                    continue;
                }
                catch (WorkflowRunEventSinkCompletedException)
                {
                    context.DetachLiveSink(sink);
                }
                catch (InvalidOperationException)
                {
                    context.DetachLiveSink(sink);
                }
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
