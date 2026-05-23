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
        // Refactor (iter41/cluster-041-command-observation-projection-activation):
        //   Old pattern: command observation binders ensure/activate projection/readmodel sessions before dispatch.
        //   New principle: observation binders attach only to existing projection-owned sessions;
        //   activation happens in projection-owned startup/background/committed-state lifecycle.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<WorkflowRunEventEnvelope>();

        try
        {
            var attachment = await _projectionPort.AttachExistingActorProjectionAsync(
                target.ActorId,
                context.CommandId,
                sink,
                ct);

            if (attachment == null)
                return await FailProjectionUnavailableAsync(target, sink);

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

    private static async Task<CommandObservationBindingResult<WorkflowChatRunStartError>> FailProjectionUnavailableAsync(
        WorkflowRunCommandTarget target,
        IEventSink<WorkflowRunEventEnvelope> sink)
    {
        await target.RollbackCreatedActorsAsync(CancellationToken.None);
        await sink.DisposeAsync();
        return CommandObservationBindingResult<WorkflowChatRunStartError>.Failure(
            WorkflowChatRunStartError.ProjectionDisabled);
    }
}
