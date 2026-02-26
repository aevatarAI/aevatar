using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunStateSnapshotEmitter
{
    Task EmitAsync(
        WorkflowRunContext runContext,
        WorkflowProjectionCompletionStatus projectionCompletionStatus,
        bool projectionCompleted,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);
}
