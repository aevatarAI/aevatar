using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowChatRunApplicationService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

    private readonly IActorRuntime _runtime;
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionRunOrchestrator _runOrchestrator;
    private readonly IWorkflowChatRequestEnvelopeFactory _requestEnvelopeFactory;
    private readonly IWorkflowRunRequestExecutor _requestExecutor;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;
    private readonly IWorkflowExecutionReportArtifactSink _reportArtifactSink;
    private readonly ILogger<WorkflowChatRunApplicationService> _logger;

    public WorkflowChatRunApplicationService(
        IActorRuntime runtime,
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionRunOrchestrator runOrchestrator,
        IWorkflowChatRequestEnvelopeFactory requestEnvelopeFactory,
        IWorkflowRunRequestExecutor requestExecutor,
        IWorkflowRunOutputStreamer outputStreamer,
        IWorkflowExecutionReportArtifactSink reportArtifactSink,
        ILogger<WorkflowChatRunApplicationService> logger)
    {
        _runtime = runtime;
        _actorResolver = actorResolver;
        _runOrchestrator = runOrchestrator;
        _requestEnvelopeFactory = requestEnvelopeFactory;
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
        var requestEnvelope = _requestEnvelopeFactory.Create(request.Prompt, runContext.RunId);
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
                runContext.ExecutionActorId,
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
            try
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
            finally
            {
                await DestroyExecutionActorSafeAsync(runContext.ExecutionActorId, CancellationToken.None);
            }
        }
    }

    private async Task<WorkflowRunContextCreateResult> CreateRunContextAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.WorkflowActor == null)
            return new WorkflowRunContextCreateResult(actorResolution.Error, null);

        var workflowActor = actorResolution.WorkflowActor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        var workflowYamlForRun = actorResolution.WorkflowYamlForRun;
        var sink = new WorkflowRunEventChannel();
        var runId = GenerateRunId();
        IActor? executionActor = null;

        WorkflowProjectionRun projectionRun;
        try
        {
            executionActor = await _runtime.CreateAsync<WorkflowExecutionGAgent>(runId, ct);
            if (executionActor.Agent is not WorkflowExecutionGAgent executionAgent)
                throw new InvalidOperationException("Created execution actor is not WorkflowExecutionGAgent.");

            executionAgent.BindWorkflowAgentId(workflowActor.Id);
            executionAgent.ConfigureWorkflow(workflowYamlForRun, workflowNameForRun);
            await _runtime.LinkAsync(workflowActor.Id, executionActor.Id, ct);

            projectionRun = await _runOrchestrator.StartAsync(
                executionActor.Id,
                workflowNameForRun,
                request.Prompt,
                sink,
                runId,
                ct);
        }
        catch
        {
            if (executionActor != null)
                await DestroyExecutionActorSafeAsync(executionActor.Id, CancellationToken.None);
            await sink.DisposeAsync();
            throw;
        }

        if (!projectionRun.Session.Enabled)
        {
            await DestroyExecutionActorSafeAsync(runId, CancellationToken.None);
            await sink.DisposeAsync();
            return new WorkflowRunContextCreateResult(
                WorkflowChatRunStartError.ProjectionDisabled,
                null);
        }

        return new WorkflowRunContextCreateResult(
            WorkflowChatRunStartError.None,
            new WorkflowRunContext
            {
                WorkflowActor = workflowActor,
                ExecutionActor = executionActor!,
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
            runContext.ExecutionActor,
            runContext.ExecutionActorId,
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

    private static string GenerateRunId() => Guid.NewGuid().ToString("N");

    private async Task DestroyExecutionActorSafeAsync(string executionActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(executionActorId))
            return;

        try
        {
            await _runtime.DestroyAsync(executionActorId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Destroy execution actor failed: {ExecutionActorId}", executionActorId);
        }
    }
}
