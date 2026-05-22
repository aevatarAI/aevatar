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
        // Refactor (iter35/cluster-039-observation-binder-attach-only):
        //   Old pattern: Command observation binders synchronously ensure and attach projection leases before dispatch,让 request/command preparation 拥有 projection lifecycle。
        //   New principle: Command observation binders 仅 attach 到 pre-existing lease/session;cold session 返回 ProjectionPending / ProjectionUnavailable;projection activation 移到 projection-owned startup / background lifecycle。
        //   删除 pre-dispatch projection activation from command binders。不新增 top-level CLAUDE.md exception。
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<WorkflowRunEventEnvelope>();

        try
        {
            if (!_projectionPort.ProjectionEnabled)
                return await FailProjectionUnavailableAsync(target, sink);

            var lease = new WorkflowExecutionObservationLease(target.ActorId, context.CommandId);
            var liveSinkLease = await _projectionPort.AttachLiveSinkAsync(
                lease,
                sink,
                ct);

            if (liveSinkLease == null)
                return await FailProjectionUnavailableAsync(target, sink);

            target.BindLiveObservation(lease, liveSinkLease, sink);
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

    // Refactor (iter35/cluster-039-observation-binder-attach-only):
    //   Old pattern: Command observation binders synchronously ensure and attach projection leases before dispatch,让 request/command preparation 拥有 projection lifecycle。
    //   New principle: Command observation binders 仅 attach 到 pre-existing lease/session;cold session 返回 ProjectionPending / ProjectionUnavailable;projection activation 移到 projection-owned startup / background lifecycle。
    //   删除 pre-dispatch projection activation from command binders。不新增 top-level CLAUDE.md exception。refactor helper, no behavior change。
    private sealed record WorkflowExecutionObservationLease(
        string ActorId,
        string CommandId) : IWorkflowExecutionProjectionLease;
}
