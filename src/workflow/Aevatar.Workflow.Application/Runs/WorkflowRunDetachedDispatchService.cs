using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDetachedDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private static readonly TimeSpan DurableCompletionPollInterval = TimeSpan.FromSeconds(1);

    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly IWorkflowRunDurableCompletionResolver _durableCompletionResolver;
    private readonly WorkflowDirectFallbackPolicy _fallbackPolicy;
    private readonly ILogger<WorkflowRunDetachedDispatchService> _logger;

    public WorkflowRunDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        IWorkflowRunDurableCompletionResolver durableCompletionResolver,
        WorkflowDirectFallbackPolicy fallbackPolicy,
        ILogger<WorkflowRunDetachedDispatchService>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline;
        _durableCompletionResolver = durableCompletionResolver;
        _fallbackPolicy = fallbackPolicy;
        _logger = logger ?? NullLogger<WorkflowRunDetachedDispatchService>.Instance;
    }

    public async Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await DispatchWithoutFallbackAsync(command, ct);
        }
        catch (Exception ex) when (_fallbackPolicy.ShouldFallback(command, ex))
        {
            var fallbackRequest = _fallbackPolicy.ToFallbackRequest(command);
            _logger.LogWarning(ex, "Workflow detached dispatch failed and falls back to direct. workflow={WorkflowName}, actorId={ActorId}, hasInlineYamls={HasInlineYamls}", command.WorkflowName ?? "<null>", command.ActorId ?? "<null>", command.WorkflowYamls is { Count: > 0 });
            return await DispatchWithoutFallbackAsync(fallbackRequest, ct);
        }
    }

    private async Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchWithoutFallbackAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct)
    {
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
                    await target.DetachLiveObservationAsync(CancellationToken.None);
                    destroyCreatedActors = await WaitForDurableCompletionAsync(
                        receipt.ActorId,
                        CancellationToken.None);
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
                        _logger.LogWarning(ex, "Detached workflow run cleanup failed. actorId={ActorId}, commandId={CommandId}", receipt.ActorId, receipt.CommandId);
                    }
                }
            },
            CancellationToken.None);
    }

    private async Task<bool> WaitForDurableCompletionAsync(
        string actorId,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DurableCompletionPollInterval);
        while (true)
        {
            var durableCompletion = await _durableCompletionResolver.ResolveAsync(actorId, ct);
            if (durableCompletion.HasTerminalStatus)
                return true;

            await timer.WaitForNextTickAsync(ct);
        }
    }
}
