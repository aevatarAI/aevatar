using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunAcceptedReceiptFactory
    : ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
{
    public WorkflowChatRunAcceptedReceipt Create(
        WorkflowRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new WorkflowChatRunAcceptedReceipt(
            target.ActorId,
            target.WorkflowName,
            context.CommandId,
            context.CorrelationId);
    }
}
