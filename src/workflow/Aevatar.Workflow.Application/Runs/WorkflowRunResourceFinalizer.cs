using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunResourceFinalizer : IWorkflowRunResourceFinalizer
{
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;

    public WorkflowRunResourceFinalizer(IWorkflowExecutionProjectionLifecyclePort projectionPort)
    {
        _projectionPort = projectionPort;
    }

    public async Task FinalizeAsync(
        WorkflowRunContext runContext,
        Task processingTask,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runContext);
        ArgumentNullException.ThrowIfNull(processingTask);
        ct.ThrowIfCancellationRequested();

        if (!runContext.HasLiveDelivery || runContext.Sink == null)
        {
            await WorkflowRunTaskAwaiter.AwaitIgnoringCancellationAsync(processingTask);
            return;
        }

        await _projectionPort.DetachReleaseAndDisposeAsync(
            runContext.ProjectionLease,
            runContext.Sink,
            () => WorkflowRunTaskAwaiter.AwaitIgnoringCancellationAsync(processingTask),
            CancellationToken.None);
    }
}
