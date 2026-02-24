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

        try
        {
            await _projectionPort.DetachLiveSinkAsync(runContext.ProjectionLease, runContext.Sink, CancellationToken.None);
            await AwaitProcessingTaskSafeAsync(processingTask);
            await _projectionPort.ReleaseActorProjectionAsync(runContext.ProjectionLease, CancellationToken.None);
        }
        finally
        {
            runContext.Sink.Complete();
            await runContext.Sink.DisposeAsync();
        }
    }

    private static async Task AwaitProcessingTaskSafeAsync(Task processingTask)
    {
        try
        {
            await processingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
