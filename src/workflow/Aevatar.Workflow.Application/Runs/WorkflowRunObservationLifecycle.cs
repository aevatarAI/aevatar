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
                return await FailProjectionUnavailableAsync(sink);

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink);
            return CommandObservationBindingResult<WorkflowChatRunStartError>.Success();
        }
        catch (Exception ex)
        {
            await DisposeSinkBeforeRethrowAsync(sink, ex).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<CommandObservationBindingResult<WorkflowChatRunStartError>> FailProjectionUnavailableAsync(
        IEventSink<WorkflowRunEventEnvelope> sink)
    {
        await sink.DisposeAsync();
        return CommandObservationBindingResult<WorkflowChatRunStartError>.Failure(
            WorkflowChatRunStartError.ProjectionUnavailable);
    }

    private static async Task DisposeSinkBeforeRethrowAsync(
        IEventSink<WorkflowRunEventEnvelope> sink,
        Exception bindException)
    {
        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException)
        {
            throw new AggregateException(
                "Workflow run observation bind failed and sink disposal also failed.",
                bindException,
                disposeException);
        }
    }
}
