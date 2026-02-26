using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunExecutionEngine : IWorkflowRunExecutionEngine
{
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _requestEnvelopeFactory;
    private readonly IWorkflowRunRequestExecutor _requestExecutor;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;
    private readonly IWorkflowRunCompletionPolicy _completionPolicy;
    private readonly IWorkflowRunResourceFinalizer _resourceFinalizer;
    private readonly IWorkflowRunStateSnapshotEmitter _stateSnapshotEmitter;

    public WorkflowRunExecutionEngine(
        ICommandEnvelopeFactory<WorkflowChatRunRequest> requestEnvelopeFactory,
        IWorkflowRunRequestExecutor requestExecutor,
        IWorkflowRunOutputStreamer outputStreamer,
        IWorkflowRunCompletionPolicy completionPolicy,
        IWorkflowRunResourceFinalizer resourceFinalizer,
        IWorkflowRunStateSnapshotEmitter stateSnapshotEmitter)
    {
        _requestEnvelopeFactory = requestEnvelopeFactory;
        _requestExecutor = requestExecutor;
        _outputStreamer = outputStreamer;
        _completionPolicy = completionPolicy;
        _resourceFinalizer = resourceFinalizer;
        _stateSnapshotEmitter = stateSnapshotEmitter;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowRunContext runContext,
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runContext);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var started = runContext.ToStarted();
        var requestEnvelope = _requestEnvelopeFactory.CreateEnvelope(request, runContext.CommandContext);
        var processingTask = ProcessEnvelopeAsync(runContext, requestEnvelope, ct);
        var projectionCompleted = false;
        var projectionCompletionStatus = WorkflowProjectionCompletionStatus.Unknown;

        try
        {
            if (onStartedAsync != null)
                await onStartedAsync(started, ct);

            await _outputStreamer.StreamAsync(
                runContext.Sink,
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

            await JoinProcessingTaskAsync(processingTask);
            if (!projectionCompleted)
                projectionCompletionStatus = WorkflowProjectionCompletionStatus.Failed;

            await _stateSnapshotEmitter.EmitAsync(
                runContext,
                projectionCompletionStatus,
                projectionCompleted,
                emitAsync,
                ct);

            var result = new WorkflowChatRunFinalizeResult(projectionCompletionStatus, projectionCompleted);
            return new WorkflowChatRunExecutionResult(
                WorkflowChatRunStartError.None,
                started,
                result);
        }
        finally
        {
            await _resourceFinalizer.FinalizeAsync(runContext, processingTask, CancellationToken.None);
        }
    }

    private Task ProcessEnvelopeAsync(
        WorkflowRunContext runContext,
        EventEnvelope requestEnvelope,
        CancellationToken ct) =>
        _requestExecutor.ExecuteAsync(
            runContext.Actor,
            runContext.ActorId,
            requestEnvelope,
            runContext.Sink,
            ct);

    private static async Task JoinProcessingTaskAsync(Task processingTask)
    {
        try
        {
            await processingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
