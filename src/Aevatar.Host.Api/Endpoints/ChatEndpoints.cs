// ─────────────────────────────────────────────────────────────
// ChatEndpoints — "用户只关心 chat" 的极简 API
//
// POST /api/chat  → 调用 workflow 应用服务，SSE 返回 AGUI 事件
// GET  /api/agents → 列出活跃 Agent
// GET  /api/workflows → 列出可用工作流
// ─────────────────────────────────────────────────────────────

using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
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
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var projectionService = app.ServiceProvider.GetRequiredService<IWorkflowExecutionProjectionService>();
        var group = app.MapGroup("/api").WithTags("Chat");

        group.MapPost("/chat", HandleChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/ws/chat", HandleChatWebSocket);
        ChatQueryEndpoints.Map(group, projectionService);

        return app;
    }

    // ─── POST /api/chat ───

    private static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        IWorkflowChatRunApplicationService chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        // ─── 1. 创建/复用 Actor 并启动投影会话 ───
        await using var sink = new AGUIEventChannel();
        var logger = loggerFactory?.CreateLogger("Aevatar.Host.Api.Chat");
        var preparation = await chatRunService.PrepareAsync(
            new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
            sink,
            ct);
        if (preparation.Error != WorkflowChatRunStartError.None || preparation.Execution == null)
        {
            http.Response.StatusCode = preparation.Error switch
            {
                WorkflowChatRunStartError.AgentNotFound => StatusCodes.Status404NotFound,
                WorkflowChatRunStartError.WorkflowNotFound => StatusCodes.Status404NotFound,
                WorkflowChatRunStartError.AgentTypeNotSupported => StatusCodes.Status400BadRequest,
                WorkflowChatRunStartError.ProjectionDisabled => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            };
            return;
        }

        var execution = preparation.Execution;

        // ─── 2. 准备 SSE 响应 ───
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        await using var writer = new AGUISseWriter(http.Response);
        var projectionsFinalized = false;

        try
        {
            // ─── 3. 流式写出 AGUI 事件 ───
            try
            {
                await chatRunService.StreamEventsUntilTerminalAsync(
                    sink,
                    execution.RunId,
                    evt => writer.WriteAsync(evt, ct),
                    ct);
            }
            catch (OperationCanceledException) { /* 客户端断开 */ }

            // ─── 4. CQRS 投影收尾 + 写执行报告（artifacts/workflow-executions） ───
            try
            {
                var finalizeResult = await chatRunService.FinalizeProjectionAsync(
                    execution,
                    CancellationToken.None);
                projectionsFinalized = true;

                await chatRunService.WriteArtifactsBestEffortAsync(
                    finalizeResult,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to finalize CQRS projection or write chat run report");
            }
        }
        finally
        {
            await chatRunService.RollbackAndJoinAsync(
                execution,
                projectionsFinalized,
                CancellationToken.None);
        }
    }

    // ─── WS /api/ws/chat ───

    private static async Task HandleChatWebSocket(
        HttpContext http,
        IWorkflowChatRunApplicationService chatRunService,
        ILoggerFactory loggerFactory,
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

        var commandText = await ChatWebSocketProtocol.ReceiveTextAsync(socket, ct);
        if (string.IsNullOrWhiteSpace(commandText))
        {
            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.error",
                code = "EMPTY_COMMAND",
                message = "Command payload is required.",
            }, CancellationToken.None);
            await ChatWebSocketProtocol.CloseAsync(socket);
            return;
        }

        ChatWsCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ChatWsCommand>(commandText, ChatWebSocketProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            command = null;
        }

        if (command?.Payload == null || !string.Equals(command.Type, "chat.command", StringComparison.Ordinal))
        {
            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.error",
                code = "INVALID_COMMAND",
                message = "Expected { type: 'chat.command', payload: { prompt, workflow?, agentId? } }.",
            }, CancellationToken.None);
            await ChatWebSocketProtocol.CloseAsync(socket);
            return;
        }

        var requestId = string.IsNullOrWhiteSpace(command.RequestId)
            ? Guid.NewGuid().ToString("N")
            : command.RequestId;
        var input = command.Payload;

        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.error",
                requestId,
                code = "INVALID_PROMPT",
                message = "Prompt is required.",
            }, CancellationToken.None);
            await ChatWebSocketProtocol.CloseAsync(socket);
            return;
        }

        await using var sink = new AGUIEventChannel();
        var preparation = await chatRunService.PrepareAsync(
            new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
            sink,
            ct);
        if (preparation.Error != WorkflowChatRunStartError.None || preparation.Execution == null)
        {
            var (code, message) = preparation.Error switch
            {
                WorkflowChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
                WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
                WorkflowChatRunStartError.AgentTypeNotSupported => ("AGENT_TYPE_NOT_SUPPORTED", "Agent is not WorkflowGAgent."),
                WorkflowChatRunStartError.ProjectionDisabled => ("PROJECTION_DISABLED", "Projection pipeline is disabled."),
                _ => ("RUN_START_FAILED", "Failed to resolve actor."),
            };

            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.error",
                requestId,
                code,
                message,
            }, CancellationToken.None);
            await ChatWebSocketProtocol.CloseAsync(socket);
            return;
        }

        var execution = preparation.Execution;

        await ChatWebSocketProtocol.SendAsync(socket, new
        {
            type = "command.ack",
            requestId,
            payload = new
            {
                runId = execution.RunId,
                threadId = execution.ActorId,
                workflow = execution.WorkflowName,
            },
        }, CancellationToken.None);
        var projectionsFinalized = false;

        try
        {
            try
            {
                await chatRunService.StreamEventsUntilTerminalAsync(
                    sink,
                    execution.RunId,
                    evt => ChatWebSocketProtocol.SendAsync(socket, new
                    {
                        type = "agui.event",
                        requestId,
                        payload = evt,
                    }, CancellationToken.None),
                    ct);
            }
            catch (OperationCanceledException) { }

            try
            {
                var finalizeResult = await chatRunService.FinalizeProjectionAsync(
                    execution,
                    CancellationToken.None);
                projectionsFinalized = true;

                var report = finalizeResult.WorkflowExecutionReport;

                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "query.result",
                    requestId,
                    payload = new
                    {
                        runId = execution.RunId,
                        projectionCompletionStatus = finalizeResult.ProjectionCompletionStatus.ToString(),
                        projectionCompleted = finalizeResult.ProjectionCompleted,
                        report,
                    },
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to finalize CQRS projection for websocket run");
                await ChatWebSocketProtocol.SendAsync(socket, new
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
            await chatRunService.RollbackAndJoinAsync(
                execution,
                projectionsFinalized,
                CancellationToken.None);

            await ChatWebSocketProtocol.CloseAsync(socket);
        }
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

public sealed record ChatWsCommand
{
    public string Type { get; init; } = "chat.command";
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
