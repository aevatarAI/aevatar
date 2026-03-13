using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDetachedDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private static readonly TimeSpan DurableCompletionPollInterval = TimeSpan.FromSeconds(1);

    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> _durableCompletionResolver;
    private readonly ILogger<WorkflowRunDetachedDispatchService> _logger;

    public WorkflowRunDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> durableCompletionResolver,
        ILogger<WorkflowRunDetachedDispatchService>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
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
        StartDetachedDrain(execution.Target, execution.Receipt);
        return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(execution.Receipt);
    }

    private void StartDetachedDrain(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt)
    {
        _ = Task.Run(
            async () =>
            {
                var destroyCreatedActors = false;

                try
                {
                    try
                    {
                        await target.DetachLiveObservationAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Detached workflow run live observation detach failed. actorId={ActorId}, commandId={CommandId}",
                            receipt.ActorId,
                            receipt.CommandId);
                    }

                    try
                    {
                        await target.ReleaseAsync(
                            destroyCreatedActors: false,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Detached workflow run projection release failed. actorId={ActorId}, commandId={CommandId}",
                            receipt.ActorId,
                            receipt.CommandId);
                    }

                    destroyCreatedActors = await WaitForDurableCompletionAsync(receipt, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Detached workflow run monitoring failed. actorId={ActorId}, commandId={CommandId}",
                        receipt.ActorId,
                        receipt.CommandId);
                }
                finally
                {
                    try
                    {
                        await target.ReleaseAsync(
                            destroyCreatedActors: destroyCreatedActors,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Detached workflow run cleanup failed. actorId={ActorId}, commandId={CommandId}",
                            receipt.ActorId,
                            receipt.CommandId);
                    }
                }
            },
            CancellationToken.None);
    }

    private async Task<bool> WaitForDurableCompletionAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DurableCompletionPollInterval);
        while (true)
        {
            var durableCompletion = await _durableCompletionResolver.ResolveAsync(receipt, ct);
            if (durableCompletion.HasTerminalCompletion)
                return true;

            await timer.WaitForNextTickAsync(ct);
        }
    }
}
