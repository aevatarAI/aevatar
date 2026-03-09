using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunInteractionService : IWorkflowRunInteractionService
{
    private readonly ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchPipeline;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;
    private readonly IWorkflowRunCompletionPolicy _completionPolicy;
    private readonly IWorkflowRunStateSnapshotEmitter _stateSnapshotEmitter;
    private readonly WorkflowDirectFallbackPolicy _fallbackPolicy;
    private readonly ILogger<WorkflowRunInteractionService> _logger;

    public WorkflowRunInteractionService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchPipeline,
        IWorkflowRunOutputStreamer outputStreamer,
        IWorkflowRunCompletionPolicy completionPolicy,
        IWorkflowRunStateSnapshotEmitter stateSnapshotEmitter,
        WorkflowDirectFallbackPolicy fallbackPolicy,
        ILogger<WorkflowRunInteractionService>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline;
        _outputStreamer = outputStreamer;
        _completionPolicy = completionPolicy;
        _stateSnapshotEmitter = stateSnapshotEmitter;
        _fallbackPolicy = fallbackPolicy;
        _logger = logger ?? NullLogger<WorkflowRunInteractionService>.Instance;
    }

    public async Task<WorkflowChatRunInteractionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        try
        {
            return await ExecuteWithoutFallbackAsync(request, emitAsync, onAcceptedAsync, ct);
        }
        catch (Exception ex) when (_fallbackPolicy.ShouldFallback(request, ex))
        {
            var fallbackRequest = _fallbackPolicy.ToFallbackRequest(request);
            _logger.LogWarning(ex, "Workflow run failed and falls back to direct. workflow={WorkflowName}, actorId={ActorId}, hasInlineYamls={HasInlineYamls}", request.WorkflowName ?? "<null>", request.ActorId ?? "<null>", request.WorkflowYamls is { Count: > 0 });
            return await ExecuteWithoutFallbackAsync(fallbackRequest, emitAsync, onAcceptedAsync, ct);
        }
    }

    private async Task<WorkflowChatRunInteractionResult> ExecuteWithoutFallbackAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        var dispatch = await _dispatchPipeline.DispatchAsync(request, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return new WorkflowChatRunInteractionResult(dispatch.Error, null, null);

        var execution = dispatch.Target;
        var target = execution.Target;
        var receipt = execution.Receipt;
        var projectionCompleted = false;
        var projectionCompletionStatus = WorkflowProjectionCompletionStatus.Unknown;

        try
        {
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            await _outputStreamer.StreamAsync(
                target.RequireLiveSink(),
                async (frame, token) =>
                {
                    if (!projectionCompleted && _completionPolicy.TryResolve(frame, out var status))
                    {
                        projectionCompleted = true;
                        projectionCompletionStatus = status;
                    }

                    await emitAsync(frame, token);
                },
                ct);

            if (!projectionCompleted)
                projectionCompletionStatus = WorkflowProjectionCompletionStatus.Failed;

            await _stateSnapshotEmitter.EmitAsync(
                receipt,
                projectionCompletionStatus,
                projectionCompleted,
                emitAsync,
                ct);

            return new WorkflowChatRunInteractionResult(
                WorkflowChatRunStartError.None,
                receipt,
                new WorkflowChatRunFinalizeResult(projectionCompletionStatus, projectionCompleted));
        }
        finally
        {
            await target.ReleaseAsync(ct: CancellationToken.None);
        }
    }
}
