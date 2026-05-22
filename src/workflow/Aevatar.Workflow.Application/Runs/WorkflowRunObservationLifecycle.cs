using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunObservationLifecycle
    : ICommandObservationLifecycle<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;

    public WorkflowRunObservationLifecycle(
        IWorkflowExecutionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandObservationBindingResult<WorkflowChatRunStartError>> BindAsync(
        WorkflowChatRunRequest command,
        CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: workflow binder activated materialization and live projections during command preparation.
        //   New principle: interaction observation lifecycle starts read-side observation before dispatch without affecting dispatch-only command admission.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<WorkflowRunEventEnvelope>();

        try
        {
            if (!await target.ActivateMaterializationAsync(ct))
            {
                await target.RollbackCreatedActorsAsync(CancellationToken.None);
                return CommandObservationBindingResult<WorkflowChatRunStartError>.Failure(
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
                return CommandObservationBindingResult<WorkflowChatRunStartError>.Failure(
                    WorkflowChatRunStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink);
            return CommandObservationBindingResult<WorkflowChatRunStartError>.Success();
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
