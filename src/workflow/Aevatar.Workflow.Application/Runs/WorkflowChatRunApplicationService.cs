using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Configuration;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowChatRunApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionRunOrchestrator _runOrchestrator;
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly ILogger<WorkflowChatRunApplicationService> _logger;

    public WorkflowChatRunApplicationService(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionRunOrchestrator runOrchestrator,
        IWorkflowExecutionProjectionService projectionService,
        ILogger<WorkflowChatRunApplicationService> logger)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
        _runOrchestrator = runOrchestrator;
        _projectionService = projectionService;
        _logger = logger;
    }

    public async Task<WorkflowChatRunPreparationResult> PrepareAsync(
        WorkflowChatRunRequest request,
        IAGUIEventSink sink,
        CancellationToken ct = default)
    {
        var actorResolution = await ResolveOrCreateActorAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return new WorkflowChatRunPreparationResult(actorResolution.Error, null);

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        var projectionRun = await _runOrchestrator.StartAsync(
            actor.Id,
            workflowNameForRun,
            request.Prompt,
            sink,
            ct);

        if (!projectionRun.Session.Enabled)
            return new WorkflowChatRunPreparationResult(WorkflowChatRunStartError.ProjectionDisabled, null);

        var runId = projectionRun.RunId;
        var requestEnvelope = CreateChatRequestEnvelope(request.Prompt, runId);
        var processingTask = ProcessEnvelopeAsync(actor, requestEnvelope, sink, runId, ct);

        return new WorkflowChatRunPreparationResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunExecution(
                actor.Id,
                workflowNameForRun,
                runId,
                projectionRun,
                processingTask));
    }

    public async Task StreamEventsUntilTerminalAsync(
        IAGUIEventSink sink,
        string runId,
        Func<AGUIEvent, Task> emitAsync,
        CancellationToken ct = default)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
        {
            await emitAsync(evt);
            if (IsTerminalEventForRun(evt, runId))
                break;
        }
    }

    public async Task<WorkflowProjectionFinalizeResult> FinalizeProjectionAsync(
        WorkflowChatRunExecution execution,
        CancellationToken ct = default)
    {
        return await _runOrchestrator.FinalizeAsync(
            execution.ProjectionRun,
            _runtime,
            execution.ActorId,
            ct);
    }

    public async Task WriteArtifactsBestEffortAsync(
        WorkflowProjectionFinalizeResult finalizeResult,
        CancellationToken ct = default)
    {
        if (!_projectionService.EnableRunReportArtifacts || finalizeResult.WorkflowExecutionReport == null)
            return;

        try
        {
            ct.ThrowIfCancellationRequested();
            var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "workflow-executions");
            var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);
            await WorkflowExecutionReportWriter.WriteAsync(finalizeResult.WorkflowExecutionReport, jsonPath, htmlPath);
            _logger.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Write workflow execution artifacts failed (best-effort).");
        }
    }

    public async Task RollbackAndJoinAsync(
        WorkflowChatRunExecution execution,
        bool projectionsFinalized,
        CancellationToken ct = default)
    {
        if (!projectionsFinalized)
            await _runOrchestrator.RollbackAsync(execution.ProjectionRun, ct);

        try
        {
            await execution.ProcessingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public bool IsTerminalEventForRun(AGUIEvent evt, string runId)
    {
        return evt switch
        {
            RunFinishedEvent finished => string.Equals(finished.RunId, runId, StringComparison.Ordinal),
            RunErrorEvent error => string.Equals(error.RunId, runId, StringComparison.Ordinal),
            _ => false,
        };
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
        IActor actor,
        EventEnvelope requestEnvelope,
        IAGUIEventSink sink,
        string runId,
        CancellationToken ct)
    {
        return ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                await actor.HandleEventAsync(requestEnvelope, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workflow execution failed for actor {ActorId}", actor.Id);
                try
                {
                    sink.Push(new RunErrorEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Message = "工作流执行异常",
                        RunId = runId,
                        Code = "INTERNAL_ERROR",
                    });
                }
                catch (InvalidOperationException)
                {
                }

                sink.Complete();
            }
        }
    }

    private static EventEnvelope CreateChatRequestEnvelope(string prompt, string runId)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = CreateInternalChatSessionId(),
        };
        chatRequest.Metadata[ChatRequestMetadataKeys.RunId] = runId;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            PublisherId = "api",
            Direction = EventDirection.Self,
        };
    }

    private static string CreateInternalChatSessionId() => $"chat-{Guid.NewGuid():N}";
}

internal sealed record WorkflowActorResolutionResult(
    IActor? Actor,
    string WorkflowNameForRun,
    WorkflowChatRunStartError Error);
