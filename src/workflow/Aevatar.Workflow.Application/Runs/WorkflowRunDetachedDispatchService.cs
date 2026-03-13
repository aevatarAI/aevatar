using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDetachedDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly IWorkflowRunDetachedCleanupScheduler _cleanupScheduler;
    private readonly ILogger<WorkflowRunDetachedDispatchService> _logger;

    public WorkflowRunDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        IWorkflowRunDetachedCleanupScheduler cleanupScheduler,
        ILogger<WorkflowRunDetachedDispatchService>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _cleanupScheduler = cleanupScheduler ?? throw new ArgumentNullException(nameof(cleanupScheduler));
        _logger = logger ?? NullLogger<WorkflowRunDetachedDispatchService>.Instance;
    }

    public async Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(dispatch.Error);

        var execution = dispatch.Target;
        await DetachLiveObservationAsync(execution.Target, execution.Receipt, ct);
        if (await TryScheduleDetachedCleanupAsync(execution.Target, execution.Receipt, ct))
            await ReleaseDetachedSessionOwnershipAsync(execution.Target, execution.Receipt);

        return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(execution.Receipt);
    }

    private async Task DetachLiveObservationAsync(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        try
        {
            await target.DetachLiveObservationAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow run live observation detach failed. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
        }
    }

    private async Task<bool> TryScheduleDetachedCleanupAsync(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        try
        {
            await _cleanupScheduler.ScheduleAsync(
                new WorkflowRunDetachedCleanupRequest(
                    target.ActorId,
                    target.WorkflowName,
                    receipt.CommandId,
                    target.CreatedActorIds),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Detached workflow cleanup scheduling failed after dispatch acceptance. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
            return false;
        }
    }

    private async Task ReleaseDetachedSessionOwnershipAsync(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt)
    {
        try
        {
            await target.ReleaseDetachedSessionOwnershipAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow projection ownership transfer failed. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
        }
    }
}
