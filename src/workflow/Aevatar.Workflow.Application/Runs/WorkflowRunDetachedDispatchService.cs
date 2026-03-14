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

        var prepared = await _dispatchPipeline.PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target == null)
            return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(prepared.Error);

        var execution = prepared.Target;
        if (!await TryScheduleDetachedCleanupAsync(execution.Target, execution.Receipt, ct))
        {
            await CleanupPreparedDispatchAsync(execution.Target, execution.Receipt);
            return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.DetachedCleanupUnavailable);
        }

        try
        {
            await _dispatchPipeline.DispatchPreparedAsync(execution, ct);
        }
        catch
        {
            if (execution.Target.DispatchFailureCleanupCompleted)
            {
                await TryDiscardDetachedCleanupAsync(execution.Target, execution.Receipt);
            }
            else
            {
                _logger.LogWarning(
                    "Detached workflow cleanup rollback did not complete after dispatch failure; keeping durable cleanup scheduled. actorId={ActorId}, commandId={CommandId}",
                    execution.Receipt.ActorId,
                    execution.Receipt.CommandId);
            }
            throw;
        }

        await TryMarkDetachedCleanupAcceptedAsync(execution.Receipt, ct);
        await DetachLiveObservationAsync(execution.Target, execution.Receipt, ct);
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
                "Detached workflow cleanup scheduling failed before detached dispatch. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
            return false;
        }
    }

    private async Task CleanupPreparedDispatchAsync(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt)
    {
        try
        {
            await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow prepared cleanup failed. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
        }
    }

    private async Task TryDiscardDetachedCleanupAsync(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt)
    {
        try
        {
            await _cleanupScheduler.DiscardAsync(
                new WorkflowRunDetachedCleanupDiscardRequest(
                    target.ActorId,
                    receipt.CommandId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow cleanup discard failed after dispatch failure. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
        }
    }

    private async Task TryMarkDetachedCleanupAcceptedAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        try
        {
            await _cleanupScheduler.MarkDispatchAcceptedAsync(
                new WorkflowRunDetachedCleanupDispatchAcceptedRequest(
                    receipt.ActorId,
                    receipt.CommandId),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow cleanup dispatch acceptance marker failed. actorId={ActorId}, commandId={CommandId}",
                receipt.ActorId,
                receipt.CommandId);
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
