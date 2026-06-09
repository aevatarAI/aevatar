using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.RunForks;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunAcceptedReceiptFactory
    : ICommandReceiptFactory<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>
{
    public WorkflowForkRunAcceptedReceipt Create(
        WorkflowForkRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new WorkflowForkRunAcceptedReceipt(
            target.SourceRunId,
            target.ActorId,
            target.WorkflowName,
            true,
            context.CommandId,
            context.CorrelationId,
            DateTimeOffset.UtcNow);
    }
}
