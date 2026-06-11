using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunAcceptedReceiptFactory
    : ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>,
      ICommandReceiptFactory<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>
{
    public WorkflowChatRunAcceptedReceipt Create(
        WorkflowRunCommandTarget target,
        CommandContext context)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new WorkflowChatRunAcceptedReceipt(
            target.ActorId,
            target.WorkflowName,
            context.CommandId,
            context.CorrelationId);
    }

    public WorkflowChatRunAcceptedReceipt Create(
        WorkflowRunAcceptedCommandTarget target,
        CommandContext context)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new WorkflowChatRunAcceptedReceipt(
            target.ActorId,
            target.WorkflowName,
            context.CommandId,
            context.CorrelationId);
    }
}
