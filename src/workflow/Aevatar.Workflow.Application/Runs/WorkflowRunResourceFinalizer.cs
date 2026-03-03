using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.CQRS.Core.Abstractions.Streaming;

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

        await _projectionPort.DetachReleaseAndDisposeAsync(
            runContext.ProjectionLease,
            runContext.Sink,
            () => AwaitProcessingTaskSafeAsync(processingTask),
            CancellationToken.None);
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
