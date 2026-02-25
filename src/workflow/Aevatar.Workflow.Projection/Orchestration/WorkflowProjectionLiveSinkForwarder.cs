using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionLiveSinkForwarder
    : IProjectionPortLiveSinkForwarder<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>
{
    private readonly IWorkflowProjectionSinkFailurePolicy _sinkFailurePolicy;

    public WorkflowProjectionLiveSinkForwarder(IWorkflowProjectionSinkFailurePolicy sinkFailurePolicy)
    {
        _sinkFailurePolicy = sinkFailurePolicy;
    }

    public async ValueTask ForwardAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IWorkflowRunEventSink sink,
        WorkflowRunEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();

        try
        {
            await sink.PushAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var handled = await _sinkFailurePolicy.TryHandleAsync(
                runtimeLease,
                sink,
                evt,
                ex,
                CancellationToken.None);
            if (!handled)
                throw;
        }
    }
}
