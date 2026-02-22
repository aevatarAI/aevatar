namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunResourceFinalizer
{
    Task FinalizeAsync(
        WorkflowRunContext runContext,
        Task processingTask,
        CancellationToken ct = default);
}
