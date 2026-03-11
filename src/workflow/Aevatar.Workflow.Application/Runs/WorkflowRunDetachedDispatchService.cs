using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDetachedDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope> _outputStream;
    private readonly ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> _completionPolicy;
    private readonly ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> _durableCompletionResolver;
    private readonly ILogger<WorkflowRunDetachedDispatchService> _logger;

    public WorkflowRunDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope> outputStream,
        ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> completionPolicy,
        ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> durableCompletionResolver,
        ILogger<WorkflowRunDetachedDispatchService>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline;
        _outputStream = outputStream;
        _completionPolicy = completionPolicy;
        _durableCompletionResolver = durableCompletionResolver;
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
                var observedCompleted = false;
                var observedCompletion = _completionPolicy.IncompleteCompletion;
                try
                {
                    await _outputStream.PumpAsync(
                        target.RequireLiveSink().ReadAllAsync(CancellationToken.None),
                        static (_, _) => ValueTask.CompletedTask,
                        evt =>
                        {
                            if (!_completionPolicy.TryResolve(evt, out var completion))
                                return false;

                            observedCompleted = true;
                            observedCompletion = completion;
                            return true;
                        },
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Detached workflow run monitoring failed. actorId={ActorId}, commandId={CommandId}", receipt.ActorId, receipt.CommandId);
                }
                finally
                {
                    try
                    {
                        var durableCompletion = observedCompleted
                            ? CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete
                            : await _durableCompletionResolver.ResolveAsync(receipt, CancellationToken.None);

                        await target.ReleaseAfterInteractionAsync(
                            receipt,
                            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                                observedCompleted,
                                observedCompletion,
                                durableCompletion),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Detached workflow run cleanup failed. actorId={ActorId}, commandId={CommandId}", receipt.ActorId, receipt.CommandId);
                    }
                }
            },
            CancellationToken.None);
    }
}
