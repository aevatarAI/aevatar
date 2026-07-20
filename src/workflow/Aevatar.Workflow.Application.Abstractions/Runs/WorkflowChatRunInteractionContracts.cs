using Aevatar.CQRS.Core.Abstractions.Interactions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowChatRunInteractionPort
{
    Task<CommandInteractionResult<WorkflowChatInteractionAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}
