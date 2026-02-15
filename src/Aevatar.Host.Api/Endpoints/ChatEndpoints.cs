// ─────────────────────────────────────────────────────────────
// ChatEndpoints — "用户只关心 chat" 的极简 API
//
// POST /api/chat  → 创建或复用 WorkflowGAgent，SSE 返回 AGUI 事件
// GET  /api/agents → 列出活跃 Agent
// GET  /api/workflows → 列出可用工作流
// ─────────────────────────────────────────────────────────────

using Aevatar.Presentation.AGUI;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Host.Api.Orchestration;
using Aevatar.Host.Api.Reporting;
using Aevatar.Host.Api.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Configuration;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Aevatar.Host.Api.Endpoints;

/// <summary>请求体：用户发送的 chat 消息。</summary>
public sealed record ChatInput
{
    /// <summary>用户提示词。</summary>
    public required string Prompt { get; init; }

    /// <summary>工作流名称。agentId 为空时必填；未传时默认为 "direct"（直接对话）。</summary>
    public string? Workflow { get; init; }

    /// <summary>复用已有 Agent 的 ID。空则新建。</summary>
    public string? AgentId { get; init; }
}

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions WsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var projectionService = app.ServiceProvider.GetRequiredService<IWorkflowExecutionProjectionService>();
        var group = app.MapGroup("/api").WithTags("Chat");

        group.MapPost("/chat", HandleChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/ws/chat", HandleChatWebSocket);

        if (projectionService.EnableRunQueryEndpoints)
        {
            group.MapGet("/runs", ListRuns)
                .Produces(StatusCodes.Status200OK);

            group.MapGet("/runs/{runId}", GetRun)
                .Produces(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status404NotFound);
        }

        return app;
    }

    // ─── POST /api/chat ───

    private static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        IActorRuntime runtime,
        WorkflowRegistry registry,
        ILoggerFactory loggerFactory,
        IWorkflowExecutionProjectionService projectionService,
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        CancellationToken ct = default)
    {
        // ─── 1. 创建或复用 Agent ───

        var actorResolution = await ResolveOrCreateActorAsync(input, runtime, registry, ct);
        if (actorResolution.Error != ChatRunStartError.None || actorResolution.Actor == null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;

        // ─── 2. 准备 SSE 响应 ───

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        await using var sink = new AGUIEventChannel();
        await using var writer = new AGUISseWriter(http.Response);
        var logger = loggerFactory?.CreateLogger("Aevatar.Host.Api.Chat");
        var projectionRun = await projectionOrchestrator.StartAsync(
            actor.Id,
            workflowNameForRun,
            input.Prompt,
            sink,
            ct);
        var runId = projectionRun.RunId;

        // ─── 3. 发送 ChatRequestEvent（RUN_STARTED 由 StartWorkflowEvent 投影统一产出） ───

        // 构造 ChatRequestEvent → 发送到 Agent
        // run_id 是执行标识；session_id 仅用于消息流关联，不与 run_id 绑定。
        var requestEnvelope = CreateChatRequestEnvelope(input.Prompt, runId);

        // 非阻塞：启动事件处理；sink 由订阅持续推送，直到 WorkflowCompleted → RunFinishedEvent
        var processingTask = ProcessEnvelopeAsync(actor, requestEnvelope, sink, runId, logger, ct);
        var projectionsFinalized = false;

        try
        {
            // ─── 4. 流式写出 AGUI 事件 ───
            try
            {
                await foreach (var evt in sink.ReadAllAsync(ct))
                {
                    await writer.WriteAsync(evt, ct);

                    // 当前请求只在“本 run”终止，事件流本身仍可收到同 Actor 的其他 run 事件。
                    if (IsTerminalEventForRun(evt, runId)) break;
                }
            }
            catch (OperationCanceledException) { /* 客户端断开 */ }

            // ─── 5. CQRS 投影收尾 + 写执行报告（artifacts/workflow-executions） ───
            try
            {
                _ = await FinalizeProjectionAsync(
                    projectionOrchestrator,
                    projectionRun,
                    projectionService,
                    runtime,
                    actor.Id,
                    logger,
                    writeArtifacts: true,
                    CancellationToken.None);
                projectionsFinalized = true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to finalize CQRS projection or write chat run report");
            }
        }
        finally
        {
            if (!projectionsFinalized)
            {
                await projectionOrchestrator.RollbackAsync(
                    projectionRun,
                    CancellationToken.None);
            }

            try
            {
                await processingTask;
            }
            catch (OperationCanceledException) { }
        }
    }

    // ─── WS /api/ws/chat ───

    private static async Task HandleChatWebSocket(
        HttpContext http,
        IActorRuntime runtime,
        WorkflowRegistry registry,
        ILoggerFactory loggerFactory,
        IWorkflowExecutionProjectionService projectionService,
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        CancellationToken ct = default)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected websocket request.", ct);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var logger = loggerFactory?.CreateLogger("Aevatar.Host.Api.Chat.WebSocket");

        var commandText = await ReceiveWebSocketTextAsync(socket, ct);
        if (string.IsNullOrWhiteSpace(commandText))
        {
            await SendWebSocketMessageAsync(socket, new
            {
                type = "command.error",
                code = "EMPTY_COMMAND",
                message = "Command payload is required.",
            }, CancellationToken.None);
            await CloseSocketAsync(socket);
            return;
        }

        ChatWsCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ChatWsCommand>(commandText, WsJsonOptions);
        }
        catch (JsonException)
        {
            command = null;
        }

        if (command?.Payload == null || !string.Equals(command.Type, "chat.command", StringComparison.Ordinal))
        {
            await SendWebSocketMessageAsync(socket, new
            {
                type = "command.error",
                code = "INVALID_COMMAND",
                message = "Expected { type: 'chat.command', payload: { prompt, workflow?, agentId? } }.",
            }, CancellationToken.None);
            await CloseSocketAsync(socket);
            return;
        }

        var requestId = string.IsNullOrWhiteSpace(command.RequestId)
            ? Guid.NewGuid().ToString("N")
            : command.RequestId;
        var input = command.Payload;

        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            await SendWebSocketMessageAsync(socket, new
            {
                type = "command.error",
                requestId,
                code = "INVALID_PROMPT",
                message = "Prompt is required.",
            }, CancellationToken.None);
            await CloseSocketAsync(socket);
            return;
        }

        var actorResolution = await ResolveOrCreateActorAsync(input, runtime, registry, ct);
        if (actorResolution.Error != ChatRunStartError.None || actorResolution.Actor == null)
        {
            var (code, message) = actorResolution.Error switch
            {
                ChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
                ChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
                _ => ("RUN_START_FAILED", "Failed to resolve actor."),
            };

            await SendWebSocketMessageAsync(socket, new
            {
                type = "command.error",
                requestId,
                code,
                message,
            }, CancellationToken.None);
            await CloseSocketAsync(socket);
            return;
        }

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;

        await using var sink = new AGUIEventChannel();
        var projectionRun = await projectionOrchestrator.StartAsync(
            actor.Id,
            workflowNameForRun,
            input.Prompt,
            sink,
            ct);
        var runId = projectionRun.RunId;

        await SendWebSocketMessageAsync(socket, new
        {
            type = "command.ack",
            requestId,
            payload = new
            {
                runId,
                threadId = actor.Id,
                workflow = workflowNameForRun,
            },
        }, CancellationToken.None);

        var requestEnvelope = CreateChatRequestEnvelope(input.Prompt, runId);

        var processingTask = ProcessEnvelopeAsync(actor, requestEnvelope, sink, runId, logger, ct);
        var projectionsFinalized = false;

        try
        {
            try
            {
                await foreach (var evt in sink.ReadAllAsync(ct))
                {
                    await SendWebSocketMessageAsync(socket, new
                    {
                        type = "agui.event",
                        requestId,
                        payload = evt,
                    }, CancellationToken.None);

                    if (IsTerminalEventForRun(evt, runId)) break;
                }
            }
            catch (OperationCanceledException) { }

            try
            {
                var finalizeResult = await FinalizeProjectionAsync(
                    projectionOrchestrator,
                    projectionRun,
                    projectionService,
                    runtime,
                    actor.Id,
                    logger,
                    writeArtifacts: false,
                    CancellationToken.None);
                projectionsFinalized = true;

                var report = finalizeResult.WorkflowExecutionReport;

                await SendWebSocketMessageAsync(socket, new
                {
                    type = "query.result",
                    requestId,
                    payload = new
                    {
                        runId,
                        projectionCompleted = finalizeResult.ProjectionCompleted,
                        report,
                    },
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to finalize CQRS projection for websocket run");
                await SendWebSocketMessageAsync(socket, new
                {
                    type = "command.error",
                    requestId,
                    code = "PROJECTION_FINALIZE_FAILED",
                    message = "Failed to finalize projection/query.",
                }, CancellationToken.None);
            }
        }
        finally
        {
            if (!projectionsFinalized)
            {
                await projectionOrchestrator.RollbackAsync(
                    projectionRun,
                    CancellationToken.None);
            }

            try
            {
                await processingTask;
            }
            catch (OperationCanceledException) { }

            await CloseSocketAsync(socket);
        }
    }

    // ─── GET /api/agents ───

    private static async Task<IResult> ListAgents(IActorRuntime runtime)
    {
        var actors = await runtime.GetAllAsync();
        var result = new List<object>();
        foreach (var actor in actors)
        {
            var desc = await actor.Agent.GetDescriptionAsync();
            result.Add(new
            {
                id = actor.Id,
                type = actor.Agent.GetType().Name,
                description = desc,
            });
        }
        return Results.Ok(result);
    }

    // ─── GET /api/workflows ───

    private static IResult ListWorkflows(WorkflowRegistry registry) =>
        Results.Ok(registry.GetNames());

    // ─── GET /api/runs ───

    private static async Task<IResult> ListRuns(
        IWorkflowExecutionProjectionService projectionService,
        int take = 50,
        CancellationToken ct = default)
    {
        var reports = await projectionService.ListRunsAsync(take, ct);
        var items = reports.Select(r => new
        {
            r.RunId,
            r.WorkflowName,
            r.RootActorId,
            r.StartedAt,
            r.EndedAt,
            r.DurationMs,
            r.Success,
            totalSteps = r.Summary.TotalSteps,
        });
        return Results.Ok(items);
    }

    // ─── GET /api/runs/{runId} ───

    private static async Task<IResult> GetRun(
        string runId,
        IWorkflowExecutionProjectionService projectionService,
        CancellationToken ct = default)
    {
        var report = await projectionService.GetRunAsync(runId, ct);
        return report == null ? Results.NotFound() : Results.Ok(report);
    }

    private static async Task<string?> ReceiveWebSocketTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            if (result.Count > 0)
                await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }

        return null;
    }

    private static async Task SendWebSocketMessageAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.Serialize(payload, WsJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
    }

    private static async Task CloseSocketAsync(WebSocket socket)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
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
            return existing == null
                ? new ActorResolutionResult(null, workflowNameForRun, ChatRunStartError.AgentNotFound)
                : new ActorResolutionResult(existing, workflowNameForRun, ChatRunStartError.None);
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
                sink.Push(new RunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "工作流执行异常",
                    RunId = runId,
                    Code = "INTERNAL_ERROR",
                });
                sink.Complete();
            }
        }
    }

    private static async Task<WorkflowProjectionFinalizeResult> FinalizeProjectionAsync(
        IWorkflowExecutionRunOrchestrator projectionOrchestrator,
        WorkflowProjectionRun projectionRun,
        IWorkflowExecutionProjectionService projectionService,
        IActorRuntime runtime,
        string actorId,
        ILogger? logger,
        bool writeArtifacts,
        CancellationToken ct)
    {
        var finalizeResult = await projectionOrchestrator.FinalizeAsync(
            projectionRun,
            runtime,
            actorId,
            ct);

        if (writeArtifacts &&
            projectionService.EnableRunReportArtifacts &&
            finalizeResult.WorkflowExecutionReport != null)
        {
            var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "workflow-executions");
            var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);
            await WorkflowExecutionReportWriter.WriteAsync(finalizeResult.WorkflowExecutionReport, jsonPath, htmlPath);
            logger?.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
        }

        return finalizeResult;
    }

    private static EventEnvelope CreateChatRequestEnvelope(
        string prompt,
        string runId)
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

    private static string CreateInternalChatSessionId()
    {
        return $"chat-{Guid.NewGuid():N}";
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
}

internal enum ChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
}

internal sealed record ActorResolutionResult(
    IActor? Actor,
    string WorkflowNameForRun,
    ChatRunStartError Error);

public sealed record ChatWsCommand
{
    public string Type { get; init; } = "chat.command";
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
