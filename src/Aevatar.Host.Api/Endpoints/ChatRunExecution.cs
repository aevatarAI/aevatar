using Aevatar.AI.Abstractions;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Host.Api.Orchestration;
using Aevatar.Host.Api.Reporting;
using Aevatar.Host.Api.Workflows;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Host.Api.Endpoints;

internal static class ChatRunExecution
{
    public static async Task<ChatRunPreparationResult> PrepareAsync(
        ChatInput input,
        IActorRuntime runtime,
        WorkflowRegistry registry,
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        IAGUIEventSink sink,
        ILogger? logger,
        CancellationToken ct)
    {
        var actorResolution = await ResolveOrCreateActorAsync(input, runtime, registry, ct);
        if (actorResolution.Error != ChatRunStartError.None || actorResolution.Actor == null)
            return new ChatRunPreparationResult(actorResolution.Error, null);

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        var projectionRun = await projectionOrchestrator.StartAsync(
            actor.Id,
            workflowNameForRun,
            input.Prompt,
            sink,
            ct);
        if (!projectionRun.Session.Enabled)
            return new ChatRunPreparationResult(ChatRunStartError.ProjectionDisabled, null);

        var runId = projectionRun.RunId;
        var requestEnvelope = CreateChatRequestEnvelope(input.Prompt, runId);
        var processingTask = ProcessEnvelopeAsync(actor, requestEnvelope, sink, runId, logger, ct);

        return new ChatRunPreparationResult(
            ChatRunStartError.None,
            new ChatRunContext(
                actor.Id,
                workflowNameForRun,
                runId,
                projectionRun,
                processingTask));
    }

    public static async Task StreamEventsUntilTerminalAsync(
        IAGUIEventSink sink,
        string runId,
        Func<AGUIEvent, Task> emitAsync,
        CancellationToken ct)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
        {
            await emitAsync(evt);
            if (IsTerminalEventForRun(evt, runId))
                break;
        }
    }

    public static async Task<WorkflowProjectionFinalizeResult> FinalizeProjectionAsync(
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        WorkflowProjectionRun projectionRun,
        IActorRuntime runtime,
        string actorId,
        CancellationToken ct)
    {
        return await projectionOrchestrator.FinalizeAsync(
            projectionRun,
            runtime,
            actorId,
            ct);
    }

    public static async Task WriteArtifactsBestEffortAsync(
        IWorkflowExecutionProjectionService projectionService,
        WorkflowProjectionFinalizeResult finalizeResult,
        ILogger? logger,
        CancellationToken ct = default)
    {
        if (!projectionService.EnableRunReportArtifacts || finalizeResult.WorkflowExecutionReport == null)
            return;

        try
        {
            ct.ThrowIfCancellationRequested();
            var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "workflow-executions");
            var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);
            await WorkflowExecutionReportWriter.WriteAsync(finalizeResult.WorkflowExecutionReport, jsonPath, htmlPath);
            logger?.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Write workflow execution artifacts failed (best-effort).");
        }
    }

    public static async Task RollbackAndJoinAsync(
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        ChatRunContext context,
        bool projectionsFinalized,
        CancellationToken ct)
    {
        if (!projectionsFinalized)
            await projectionOrchestrator.RollbackAsync(context.ProjectionRun, ct);

        try
        {
            await context.ProcessingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static bool IsTerminalEventForRun(AGUIEvent evt, string runId)
    {
        return evt switch
        {
            RunFinishedEvent finished => string.Equals(finished.RunId, runId, StringComparison.Ordinal),
            RunErrorEvent error => string.Equals(error.RunId, runId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static async Task<ActorResolutionResult> ResolveOrCreateActorAsync(
        ChatInput input,
        IActorRuntime runtime,
        WorkflowRegistry registry,
        CancellationToken ct)
    {
        var workflowNameForRun = string.IsNullOrWhiteSpace(input.Workflow) ? "direct" : input.Workflow;

        if (!string.IsNullOrWhiteSpace(input.AgentId))
        {
            var existing = await runtime.GetAsync(input.AgentId);
            if (existing == null)
                return new ActorResolutionResult(null, workflowNameForRun, ChatRunStartError.AgentNotFound);
            if (existing.Agent is not WorkflowGAgent)
                return new ActorResolutionResult(null, workflowNameForRun, ChatRunStartError.AgentTypeNotSupported);

            return new ActorResolutionResult(existing, workflowNameForRun, ChatRunStartError.None);
        }

        var yaml = registry.GetYaml(workflowNameForRun);
        if (yaml == null)
            return new ActorResolutionResult(null, workflowNameForRun, ChatRunStartError.WorkflowNotFound);

        var actor = await runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        if (actor.Agent is WorkflowGAgent wfAgent)
            wfAgent.ConfigureWorkflow(yaml, workflowNameForRun);

        return new ActorResolutionResult(actor, workflowNameForRun, ChatRunStartError.None);
    }

    private static Task ProcessEnvelopeAsync(
        IActor actor,
        EventEnvelope requestEnvelope,
        IAGUIEventSink sink,
        string runId,
        ILogger? logger,
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
                logger?.LogWarning(ex, "Workflow execution failed for actor {ActorId}", actor.Id);
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

internal enum ChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
    AgentTypeNotSupported = 3,
    ProjectionDisabled = 4,
}

internal sealed record ActorResolutionResult(
    IActor? Actor,
    string WorkflowNameForRun,
    ChatRunStartError Error);

internal sealed record ChatRunPreparationResult(
    ChatRunStartError Error,
    ChatRunContext? Context);

internal sealed record ChatRunContext(
    string ActorId,
    string WorkflowName,
    string RunId,
    WorkflowProjectionRun ProjectionRun,
    Task ProcessingTask);
