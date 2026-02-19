using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowRunCommandService
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _requestEnvelopeFactory;
    private readonly ICommandContextPolicy _commandContextPolicy;
    private readonly IWorkflowRunRequestExecutor _requestExecutor;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;

    public WorkflowChatRunApplicationService(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> requestEnvelopeFactory,
        ICommandContextPolicy commandContextPolicy,
        IWorkflowRunRequestExecutor requestExecutor,
        IWorkflowRunOutputStreamer outputStreamer)
    {
        _actorResolver = actorResolver;
        _projectionPort = projectionPort;
        _requestEnvelopeFactory = requestEnvelopeFactory;
        _commandContextPolicy = commandContextPolicy;
        _requestExecutor = requestExecutor;
        _outputStreamer = outputStreamer;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var runContextCreateResult = await CreateRunContextAsync(request, ct);
        if (runContextCreateResult.Error != WorkflowChatRunStartError.None ||
            runContextCreateResult.Context == null)
        {
            return new WorkflowChatRunExecutionResult(runContextCreateResult.Error, null, null);
        }

        var runContext = runContextCreateResult.Context;
        var started = runContext.ToStarted();
        var requestEnvelope = _requestEnvelopeFactory.CreateEnvelope(request, runContext.CommandContext);
        var processingTask = ProcessEnvelopeAsync(runContext, requestEnvelope, ct);

        try
        {
            if (onStartedAsync != null)
                await onStartedAsync(started, ct);

            await _outputStreamer.StreamAsync(runContext.Sink, emitAsync, ct);
            await JoinProcessingTaskAsync(processingTask);
            var result = new WorkflowChatRunFinalizeResult(
                WorkflowProjectionCompletionStatus.Completed,
                true);

            return new WorkflowChatRunExecutionResult(
                WorkflowChatRunStartError.None,
                started,
                result);
        }
        finally
        {
            await _projectionPort.DetachLiveSinkAsync(runContext.ActorId, runContext.Sink, CancellationToken.None);
            await JoinProcessingTaskAsync(processingTask);
            await DisposeSinkSafeAsync(runContext.Sink);
        }
    }

    private async Task<WorkflowRunContextCreateResult> CreateRunContextAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return new WorkflowRunContextCreateResult(actorResolution.Error, null);

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        if (!_projectionPort.ProjectionEnabled)
            return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);

        var baseContext = _commandContextPolicy.Create(actor.Id);
        var metadata = new Dictionary<string, string>(baseContext.Metadata, StringComparer.Ordinal)
        {
            [WorkflowRunCommandMetadataKeys.SessionId] = baseContext.CorrelationId,
        };
        var commandContext = new CommandContext(
            baseContext.TargetId,
            baseContext.CommandId,
            baseContext.CorrelationId,
            metadata);
        var sink = new WorkflowRunEventChannel();

        try
        {
            await _projectionPort.EnsureActorProjectionAsync(
                actor.Id,
                workflowNameForRun,
                request.Prompt,
                commandContext.CommandId,
                ct);
            await _projectionPort.AttachLiveSinkAsync(actor.Id, sink, ct);
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }

        return new WorkflowRunContextCreateResult(
            WorkflowChatRunStartError.None,
            new WorkflowRunContext
            {
                Actor = actor,
                WorkflowName = workflowNameForRun,
                Sink = sink,
                CommandId = commandContext.CommandId,
                CommandContext = commandContext,
            });
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

    private static async Task DisposeSinkSafeAsync(IWorkflowRunEventSink sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
    }
}
