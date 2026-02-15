using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowChatRunApplicationService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionRunOrchestrator _runOrchestrator;
    private readonly IWorkflowChatRequestEnvelopeFactory _requestEnvelopeFactory;
    private readonly IWorkflowExecutionReportArtifactSink _reportArtifactSink;
    private readonly ILogger<WorkflowChatRunApplicationService> _logger;

    public WorkflowChatRunApplicationService(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionRunOrchestrator runOrchestrator,
        IWorkflowChatRequestEnvelopeFactory requestEnvelopeFactory,
        IWorkflowExecutionReportArtifactSink reportArtifactSink,
        ILogger<WorkflowChatRunApplicationService> logger)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
        _runOrchestrator = runOrchestrator;
        _requestEnvelopeFactory = requestEnvelopeFactory;
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

            await StreamOutputAsync(runContext, emitAsync, ct);

            var finalizeResult = await _runOrchestrator.FinalizeAsync(
                runContext.ProjectionRun,
                _runtime,
                runContext.ActorId,
                ct);
            finalized = true;

            await PersistReportBestEffortAsync(finalizeResult.WorkflowExecutionReport, ct);
            await JoinProcessingTaskAsync(processingTask);

            var result = new WorkflowChatRunFinalizeResult(
                ToCompletionStatus(finalizeResult.ProjectionCompletionStatus),
                finalizeResult.ProjectionCompleted,
                finalizeResult.WorkflowExecutionReport);

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
        var actorResolution = await ResolveOrCreateActorAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return new WorkflowRunContextCreateResult(actorResolution.Error, null);

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        var sink = new AGUIEventChannel();

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

    private async Task<WorkflowActorResolutionResult> ResolveOrCreateActorAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        var workflowNameForRun = string.IsNullOrWhiteSpace(request.WorkflowName) ? "direct" : request.WorkflowName;

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var existing = await _runtime.GetAsync(request.ActorId);
            if (existing == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

            if (existing.Agent is not WorkflowGAgent)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

            return new WorkflowActorResolutionResult(existing, workflowNameForRun, WorkflowChatRunStartError.None);
        }

        var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
        if (yaml == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

        var actor = await _runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        if (actor.Agent is WorkflowGAgent workflowAgent)
            workflowAgent.ConfigureWorkflow(yaml, workflowNameForRun);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private Task ProcessEnvelopeAsync(
        WorkflowRunContext runContext,
        EventEnvelope requestEnvelope,
        CancellationToken ct)
    {
        return ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                await runContext.Actor.HandleEventAsync(requestEnvelope, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workflow execution failed for actor {ActorId}", runContext.ActorId);
                try
                {
                    runContext.Sink.Push(new RunErrorEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Message = "工作流执行异常",
                        RunId = runContext.RunId,
                        Code = "INTERNAL_ERROR",
                    });
                }
                catch (InvalidOperationException)
                {
                }

                runContext.Sink.Complete();
            }
        }
    }

    private static async Task StreamOutputAsync(
        WorkflowRunContext runContext,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct)
    {
        await foreach (var evt in runContext.Sink.ReadAllAsync(ct))
        {
            var frame = WorkflowOutputFrameMapper.Map(evt);
            await emitAsync(frame, ct);
            if (IsTerminalEventForRun(evt, runContext.RunId))
                break;
        }
    }

    private async Task PersistReportBestEffortAsync(
        WorkflowExecutionReport? report,
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

    private static async Task DisposeSinkSafeAsync(AGUIEventChannel sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
    }

    private static bool IsTerminalEventForRun(AGUIEvent evt, string runId)
    {
        return evt switch
        {
            RunFinishedEvent finished => string.Equals(finished.RunId, runId, StringComparison.Ordinal),
            RunErrorEvent error => string.Equals(error.RunId, runId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static WorkflowProjectionCompletionStatus ToCompletionStatus(ProjectionRunCompletionStatus status)
    {
        return status switch
        {
            ProjectionRunCompletionStatus.Completed => WorkflowProjectionCompletionStatus.Completed,
            ProjectionRunCompletionStatus.TimedOut => WorkflowProjectionCompletionStatus.TimedOut,
            ProjectionRunCompletionStatus.Failed => WorkflowProjectionCompletionStatus.Failed,
            ProjectionRunCompletionStatus.Stopped => WorkflowProjectionCompletionStatus.Stopped,
            ProjectionRunCompletionStatus.NotFound => WorkflowProjectionCompletionStatus.NotFound,
            ProjectionRunCompletionStatus.Disabled => WorkflowProjectionCompletionStatus.Disabled,
            _ => WorkflowProjectionCompletionStatus.Unknown,
        };
    }
}

internal sealed record WorkflowActorResolutionResult(
    IActor? Actor,
    string WorkflowNameForRun,
    WorkflowChatRunStartError Error);
