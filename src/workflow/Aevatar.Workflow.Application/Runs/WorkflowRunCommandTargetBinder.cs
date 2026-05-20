using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetBinder
    : ICommandTargetBinder<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;

    public WorkflowRunCommandTargetBinder(
        IWorkflowExecutionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort;
    }

    public async Task<CommandTargetBindingResult<WorkflowChatRunStartError>> BindAsync(
        WorkflowChatRunRequest command,
        WorkflowRunCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: DefaultDetachedCommandDispatchService 在 accepted-only path 持有 live sink
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<WorkflowRunEventEnvelope>();

        try
        {
            if (!await target.ActivateMaterializationAsync(ct))
            {
                await target.RollbackCreatedActorsAsync(CancellationToken.None);
                return CommandTargetBindingResult<WorkflowChatRunStartError>.Failure(
                    WorkflowChatRunStartError.ProjectionDisabled);
            }

            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    context.CommandId,
                    token),
                sink,
                ct);

            if (attachment == null)
            {
                await target.RollbackCreatedActorsAsync(CancellationToken.None);
                return CommandTargetBindingResult<WorkflowChatRunStartError>.Failure(
                    WorkflowChatRunStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink);
            return CommandTargetBindingResult<WorkflowChatRunStartError>.Success();
        }
        catch (Exception ex)
        {
            var rollbackError = await TryRollbackCreatedActorsAsync(target);
            if (rollbackError == null)
                throw;

            ExceptionDispatchInfo.Capture(
                new AggregateException(
                    "Workflow run target binding failed and rollback also failed.",
                    ex,
                    rollbackError)).Throw();
            throw;
        }
    }

    private static async Task<Exception?> TryRollbackCreatedActorsAsync(WorkflowRunCommandTarget target)
    {
        try
        {
            await target.RollbackCreatedActorsAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
