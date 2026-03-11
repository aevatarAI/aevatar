using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunStateSnapshotEmitter
{
    Task EmitAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        WorkflowProjectionCompletionStatus projectionCompletionStatus,
        bool projectionCompleted,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);
}
