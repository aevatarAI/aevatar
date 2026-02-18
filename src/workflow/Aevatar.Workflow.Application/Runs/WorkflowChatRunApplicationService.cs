using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Orchestration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowChatRunApplicationService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

    private readonly IActorRuntime _runtime;
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionRunOrchestrator _runOrchestrator;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _requestEnvelopeFactory;
    private readonly ICommandContextPolicy _commandContextPolicy;
    private readonly IWorkflowRunRequestExecutor _requestExecutor;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;
    private readonly IWorkflowExecutionReportArtifactSink _reportArtifactSink;
    private readonly ILogger<WorkflowChatRunApplicationService> _logger;

    public WorkflowChatRunApplicationService(
        IActorRuntime runtime,
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionRunOrchestrator runOrchestrator,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> requestEnvelopeFactory,
        ICommandContextPolicy commandContextPolicy,
        IWorkflowRunRequestExecutor requestExecutor,
        IWorkflowRunOutputStreamer outputStreamer,
        IWorkflowExecutionReportArtifactSink reportArtifactSink,
        ILogger<WorkflowChatRunApplicationService> logger)
    {
        _runtime = runtime;
        _actorResolver = actorResolver;
        _runOrchestrator = runOrchestrator;
        _requestEnvelopeFactory = requestEnvelopeFactory;
        _commandContextPolicy = commandContextPolicy;
        _requestExecutor = requestExecutor;
        _outputStreamer = outputStreamer;
        _reportArtifactSink = reportArtifactSink;
        _logger = logger;
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
        var context = _commandContextPolicy.Create(
            runContext.ActorId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.RunId] = runContext.RunId,
                [WorkflowRunCommandMetadataKeys.SessionId] = $"session-{runContext.RunId}",
            });
        var requestEnvelope = _requestEnvelopeFactory.CreateEnvelope(request, context);
        var processingTask = ProcessEnvelopeAsync(runContext, requestEnvelope, ct);

        var finalized = false;
        try
        {
            if (onStartedAsync != null)
                await onStartedAsync(started, ct);

            await _outputStreamer.StreamAsync(runContext.Sink, runContext.RunId, emitAsync, ct);

            var finalizeResult = await _runOrchestrator.FinalizeAsync(
                runContext.ProjectionRun,
                _runtime,
                runContext.ActorId,
                ct);
            finalized = true;

            var report = finalizeResult.WorkflowExecutionReport;

            await PersistReportBestEffortAsync(report, ct);
            await JoinProcessingTaskAsync(processingTask);

            var result = new WorkflowChatRunFinalizeResult(
                finalizeResult.ProjectionCompletionStatus,
                finalizeResult.ProjectionCompleted,
                report);

            return new WorkflowChatRunExecutionResult(
                WorkflowChatRunStartError.None,
                started,
                result);
        }
        finally
        {
            if (finalized)
            {
                await DisposeSinkSafeAsync(runContext.Sink);
            }
            else
            {
                try
                {
                    await AbortCoreAsync(runContext, processingTask);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Abort workflow projection run failed during cleanup.");
                }
            }
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
        var sink = new WorkflowRunEventChannel();

        WorkflowProjectionRun projectionRun;
        try
        {
            projectionRun = await _runOrchestrator.StartAsync(
                actor.Id,
                workflowNameForRun,
                request.Prompt,
                sink,
                ct);
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }

        if (!projectionRun.Session.Enabled)
        {
            await sink.DisposeAsync();
            return new WorkflowRunContextCreateResult(
                WorkflowChatRunStartError.ProjectionDisabled,
                null);
        }

        return new WorkflowRunContextCreateResult(
            WorkflowChatRunStartError.None,
            new WorkflowRunContext
            {
                Actor = actor,
                WorkflowName = workflowNameForRun,
                ProjectionRun = projectionRun,
                Sink = sink,
            });
    }

    private Task ProcessEnvelopeAsync(
        WorkflowRunContext runContext,
        EventEnvelope requestEnvelope,
        CancellationToken ct) =>
        _requestExecutor.ExecuteAsync(
            runContext.Actor,
            runContext.ActorId,
            runContext.RunId,
            requestEnvelope,
            runContext.Sink,
            ct);

    private async Task PersistReportBestEffortAsync(
        WorkflowRunReport? report,
        CancellationToken ct)
    {
        if (report == null)
            return;

        try
        {
            await _reportArtifactSink.PersistAsync(report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Write workflow execution artifacts failed (best-effort).");
        }
    }

    private async Task AbortCoreAsync(
        WorkflowRunContext runContext,
        Task processingTask)
    {
        using var cleanupTimeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await _runOrchestrator.RollbackAsync(runContext.ProjectionRun, cleanupTimeout.Token);
        }
        finally
        {
            await JoinProcessingTaskAsync(processingTask);
            await DisposeSinkSafeAsync(runContext.Sink);
        }
    }

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
