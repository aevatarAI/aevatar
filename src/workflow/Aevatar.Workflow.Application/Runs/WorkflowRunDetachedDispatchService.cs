using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDetachedDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private static readonly TimeSpan DefaultDurableCompletionPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultDurableCompletionMonitoringTimeout = TimeSpan.FromMinutes(5);

    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> _durableCompletionResolver;
    private readonly ILogger<WorkflowRunDetachedDispatchService> _logger;
    private readonly TimeSpan _durableCompletionPollInterval;
    private readonly TimeSpan _durableCompletionMonitoringTimeout;

    public WorkflowRunDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> durableCompletionResolver,
        ILogger<WorkflowRunDetachedDispatchService>? logger = null,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _logger = logger ?? NullLogger<WorkflowRunDetachedDispatchService>.Instance;
        _durableCompletionPollInterval = behaviorOptions?.DetachedDurableCompletionPollInterval > TimeSpan.Zero
            ? behaviorOptions.DetachedDurableCompletionPollInterval
            : DefaultDurableCompletionPollInterval;
        _durableCompletionMonitoringTimeout = behaviorOptions?.DetachedDurableCompletionMonitoringTimeout > TimeSpan.Zero
            ? behaviorOptions.DetachedDurableCompletionMonitoringTimeout
            : DefaultDurableCompletionMonitoringTimeout;
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
                var durableCompletionObserved = false;

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

                    durableCompletionObserved = await WaitForDurableCompletionAsync(receipt, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Detached workflow run monitoring failed. actorId={ActorId}, commandId={CommandId}",
                        receipt.ActorId,
                        receipt.CommandId);
                }

                if (!durableCompletionObserved)
                    return;

                try
                {
                    await target.ReleaseAsync(
                        destroyCreatedActors: true,
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
            },
            CancellationToken.None);
    }

    private async Task<bool> WaitForDurableCompletionAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_durableCompletionPollInterval);
        var resolveTimeoutWarningLogged = false;
        while (true)
        {
            using var resolveCts = CreateDurableCompletionResolveTimeoutSource(ct);
            try
            {
                var durableCompletion = await _durableCompletionResolver.ResolveAsync(receipt, resolveCts?.Token ?? ct);
                if (durableCompletion.HasTerminalCompletion)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException) when (resolveCts?.IsCancellationRequested == true)
            {
                if (!resolveTimeoutWarningLogged)
                {
                    resolveTimeoutWarningLogged = true;
                    _logger.LogWarning(
                        "Detached workflow run durable completion observation timed out; background monitoring will keep retrying. actorId={ActorId}, commandId={CommandId}, timeoutMs={TimeoutMs}",
                        receipt.ActorId,
                        receipt.CommandId,
                        (long)_durableCompletionMonitoringTimeout.TotalMilliseconds);
                }
            }

            await timer.WaitForNextTickAsync(ct);
        }
    }

    private CancellationTokenSource? CreateDurableCompletionResolveTimeoutSource(CancellationToken ct)
    {
        if (_durableCompletionMonitoringTimeout <= TimeSpan.Zero ||
            _durableCompletionMonitoringTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolveCts.CancelAfter(_durableCompletionMonitoringTimeout);
        return resolveCts;
    }
}
