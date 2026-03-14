using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunControlAcceptedReceiptFactory
    : ICommandReceiptFactory<WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt>
{
    public WorkflowRunControlAcceptedReceipt Create(
        WorkflowRunControlCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new WorkflowRunControlAcceptedReceipt(
            target.ActorId,
            target.RunId,
            context.CommandId,
            context.CorrelationId);
    }
}
