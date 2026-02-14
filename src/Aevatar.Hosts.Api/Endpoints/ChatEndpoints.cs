// ─────────────────────────────────────────────────────────────
// ChatEndpoints — "用户只关心 chat" 的极简 API
//
// POST /api/chat  → 创建或复用 WorkflowGAgent，SSE 返回 AGUI 事件
// GET  /api/agents → 列出活跃 Agent
// GET  /api/workflows → 列出可用工作流
// ─────────────────────────────────────────────────────────────

using Aevatar.Presentation.AGUI;
using Aevatar.AI.Abstractions;
using Aevatar.Cqrs.Projections.Abstractions;
using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Hosts.Api.Projection;
using Aevatar.Hosts.Api.Reporting;
using Aevatar.Hosts.Api.Workflows;
using Aevatar.Workflows.Core;
using Aevatar.Configuration;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Aevatar.Hosts.Api.Endpoints;

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
        var projectionService = app.ServiceProvider.GetRequiredService<IChatRunProjectionService>();
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
        IStreamProvider streams,
        WorkflowRegistry registry,
        ILoggerFactory loggerFactory,
        IChatRunProjectionService projectionService,
        CancellationToken ct = default)
    {
        // ─── 1. 创建或复用 Agent ───

        var workflowNameForRun = string.IsNullOrWhiteSpace(input.Workflow) ? "direct" : input.Workflow;

        IActor actor;
        if (!string.IsNullOrWhiteSpace(input.AgentId))
        {
            var existing = await runtime.GetAsync(input.AgentId);
            if (existing == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            actor = existing;
        }
        else
        {
            var yaml = registry.GetYaml(workflowNameForRun);
            if (yaml == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            actor = await runtime.CreateAsync<WorkflowGAgent>(ct: ct);

            // 设置 workflow YAML 到 Agent State
            if (actor.Agent is WorkflowGAgent wfAgent)
                wfAgent.ConfigureWorkflow(yaml, workflowNameForRun);
        }

        // ─── 2. 准备 SSE 响应 ───

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        await using var sink = new AgUiEventChannel();
        await using var writer = new AgUiSseWriter(http.Response);
        var logger = loggerFactory?.CreateLogger("Aevatar.Hosts.Api.Chat");

        // ─── 3. 初始化 CQRS Projection Session ───

        var projectionSession = await projectionService.StartAsync(
            actor.Id,
            workflowNameForRun,
            input.Prompt,
            ct);

        // ─── 4. 订阅 Agent Stream（捕获事件并映射 AG-UI） ───

        var stream = streams.GetStream(actor.Id);
        var subscription = await stream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            foreach (var agUiEvt in AgUiProjector.Project(envelope))
                sink.Push(agUiEvt);
        }, ct);

        // ─── 5. 发送 RUN_STARTED + ChatRequestEvent ───

        var ts = projectionSession.StartedAt.ToUnixTimeMilliseconds();

        await writer.WriteAsync(new RunStartedEvent
        {
            Timestamp = ts,
            ThreadId = actor.Id,
            RunId = projectionSession.RunId,
        }, ct);

        // 构造 ChatRequestEvent → 发送到 Agent
        var chatEvt = new ChatRequestEvent { Prompt = input.Prompt };
        var requestEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatEvt),
            PublisherId = "api",
            Direction = EventDirection.Self,
        };

        // 非阻塞：启动事件处理；sink 由订阅持续推送，直到 WorkflowCompleted → RunFinishedEvent
        var processingTask = ProcessEnvelopeAsync();
        async Task ProcessEnvelopeAsync()
        {
            try
            {
                await actor.HandleEventAsync(requestEnvelope, ct);
            }
            catch (Exception)
            {
                sink.Push(new RunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "工作流执行异常",
                    Code = "INTERNAL_ERROR",
                });
                sink.Complete();
            }
        }

        // ─── 6. 流式写出 AGUI 事件 ───

        try
        {
            await foreach (var evt in sink.ReadAllAsync(ct))
            {
                await writer.WriteAsync(evt, ct);

                // RUN_FINISHED / RUN_ERROR 是终止信号
                if (evt is RunFinishedEvent or RunErrorEvent) break;
            }
        }
        catch (OperationCanceledException) { /* 客户端断开 */ }

        // ─── 7. CQRS 投影收尾 + 写执行报告（artifacts/chat-runs） ───
        try
        {
            var projectionCompleted = await projectionService.WaitForRunProjectionCompletedAsync(
                projectionSession.RunId,
                CancellationToken.None);
            if (!projectionCompleted)
            {
                logger?.LogWarning(
                    "Projection completion signal timeout for run {RunId}; continue with finalize/query.",
                    projectionSession.RunId);
            }

            var topology = await BuildTopologyAsync(runtime);
            var finalizedReport = await projectionService.CompleteAsync(
                projectionSession,
                topology,
                CancellationToken.None);

            var report = finalizedReport;
            if (report == null && projectionService.EnableRunQueryEndpoints)
            {
                report = await projectionService.GetRunAsync(
                    projectionSession.RunId,
                    CancellationToken.None);
            }

            if (report != null && projectionService.EnableRunReportArtifacts)
            {
                var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "chat-runs");
                var (jsonPath, htmlPath) = ChatRunReportWriter.BuildDefaultPaths(outputDir);
                await ChatRunReportWriter.WriteAsync(report, jsonPath, htmlPath);
                logger?.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to finalize CQRS projection or write chat run report");
        }

        // ─── 8. 清理 ───

        await subscription.DisposeAsync();
        await processingTask;
    }

    // ─── WS /api/ws/chat ───

    private static async Task HandleChatWebSocket(
        HttpContext http,
        IActorRuntime runtime,
        IStreamProvider streams,
        WorkflowRegistry registry,
        ILoggerFactory loggerFactory,
        IChatRunProjectionService projectionService,
        CancellationToken ct = default)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected websocket request.", ct);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var logger = loggerFactory?.CreateLogger("Aevatar.Hosts.Api.Chat.WebSocket");

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

        var workflowNameForRun = string.IsNullOrWhiteSpace(input.Workflow) ? "direct" : input.Workflow;

        IActor actor;
        if (!string.IsNullOrWhiteSpace(input.AgentId))
        {
            var existing = await runtime.GetAsync(input.AgentId);
            if (existing == null)
            {
                await SendWebSocketMessageAsync(socket, new
                {
                    type = "command.error",
                    requestId,
                    code = "AGENT_NOT_FOUND",
                    message = "Agent not found.",
                }, CancellationToken.None);
                await CloseSocketAsync(socket);
                return;
            }

            actor = existing;
        }
        else
        {
            var yaml = registry.GetYaml(workflowNameForRun);
            if (yaml == null)
            {
                await SendWebSocketMessageAsync(socket, new
                {
                    type = "command.error",
                    requestId,
                    code = "WORKFLOW_NOT_FOUND",
                    message = "Workflow not found.",
                }, CancellationToken.None);
                await CloseSocketAsync(socket);
                return;
            }

            actor = await runtime.CreateAsync<WorkflowGAgent>(ct: ct);
            if (actor.Agent is WorkflowGAgent wfAgent)
                wfAgent.ConfigureWorkflow(yaml, workflowNameForRun);
        }

        await using var sink = new AgUiEventChannel();
        var projectionSession = await projectionService.StartAsync(
            actor.Id,
            workflowNameForRun,
            input.Prompt,
            ct);

        var stream = streams.GetStream(actor.Id);
        var subscription = await stream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            foreach (var agUiEvt in AgUiProjector.Project(envelope))
                sink.Push(agUiEvt);
        }, ct);

        await SendWebSocketMessageAsync(socket, new
        {
            type = "command.ack",
            requestId,
            payload = new
            {
                runId = projectionSession.RunId,
                threadId = actor.Id,
                workflow = workflowNameForRun,
            },
        }, CancellationToken.None);

        await SendWebSocketMessageAsync(socket, new
        {
            type = "agui.event",
            requestId,
            payload = new RunStartedEvent
            {
                Timestamp = projectionSession.StartedAt.ToUnixTimeMilliseconds(),
                ThreadId = actor.Id,
                RunId = projectionSession.RunId,
            },
        }, CancellationToken.None);

        var requestEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent { Prompt = input.Prompt }),
            PublisherId = "api",
            Direction = EventDirection.Self,
        };

        var processingTask = ProcessEnvelopeAsync();
        async Task ProcessEnvelopeAsync()
        {
            try
            {
                await actor.HandleEventAsync(requestEnvelope, ct);
            }
            catch (Exception)
            {
                sink.Push(new RunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "工作流执行异常",
                    Code = "INTERNAL_ERROR",
                });
                sink.Complete();
            }
        }

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

                if (evt is RunFinishedEvent or RunErrorEvent) break;
            }
        }
        catch (OperationCanceledException) { }

        try
        {
            var projectionCompleted = await projectionService.WaitForRunProjectionCompletedAsync(
                projectionSession.RunId,
                CancellationToken.None);

            var topology = await BuildTopologyAsync(runtime);
            _ = await projectionService.CompleteAsync(
                projectionSession,
                topology,
                CancellationToken.None);

            ChatRunReport? report = null;
            if (projectionService.EnableRunQueryEndpoints)
            {
                report = await projectionService.GetRunAsync(
                    projectionSession.RunId,
                    CancellationToken.None);
            }

            await SendWebSocketMessageAsync(socket, new
            {
                type = "query.result",
                requestId,
                payload = new
                {
                    runId = projectionSession.RunId,
                    projectionCompleted,
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

        await subscription.DisposeAsync();
        await processingTask;
        await CloseSocketAsync(socket);
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
        IChatRunProjectionService projectionService,
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
        IChatRunProjectionService projectionService,
        CancellationToken ct = default)
    {
        var report = await projectionService.GetRunAsync(runId, ct);
        return report == null ? Results.NotFound() : Results.Ok(report);
    }

    private static async Task<List<ChatTopologyEdge>> BuildTopologyAsync(IActorRuntime runtime)
    {
        var allActors = await runtime.GetAllAsync();
        var topology = new List<ChatTopologyEdge>();
        foreach (var actor in allActors)
        {
            var parent = await actor.GetParentIdAsync();
            if (!string.IsNullOrWhiteSpace(parent))
                topology.Add(new ChatTopologyEdge(parent, actor.Id));
        }

        return topology;
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
}

public sealed record ChatWsCommand
{
    public string Type { get; init; } = "chat.command";
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
