namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowRunInteractionService
{
    Task<WorkflowChatRunInteractionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}
