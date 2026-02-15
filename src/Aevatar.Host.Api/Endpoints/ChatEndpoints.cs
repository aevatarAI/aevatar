using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Host.Api.Endpoints;

public sealed record ChatInput
{
    public required string Prompt { get; init; }
    public string? Workflow { get; init; }
    public string? AgentId { get; init; }
}

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");

        group.MapPost("/chat", HandleChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/ws/chat", HandleChatWebSocket);
        ChatQueryEndpoints.Map(group);

        return app;
    }

    internal static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        IWorkflowChatRunApplicationService chatRunService,
        CancellationToken ct = default)
    {
        var responseStarted = false;

        ValueTask StartSseAsync(CancellationToken token)
        {
            if (responseStarted)
                return ValueTask.CompletedTask;

            responseStarted = true;
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            return new ValueTask(http.Response.StartAsync(token));
        }

        try
        {
            var result = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
                async (frame, token) =>
                {
                    await StartSseAsync(token);
                    await WriteSseFrameAsync(http.Response, frame, token);
                },
                onStartedAsync: (_, token) => StartSseAsync(token),
                ct);

            if (result.Error != WorkflowChatRunStartError.None && !responseStarted)
                http.Response.StatusCode = ToHttpStatusCode(result.Error);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static async Task HandleChatWebSocket(
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

        try
        {
            var commandText = await ChatWebSocketProtocol.ReceiveTextAsync(socket, ct);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.error",
                    code = "EMPTY_COMMAND",
                    message = "Command payload is required.",
                }, ct);
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
                }, ct);
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
                }, ct);
                return;
            }

            var executionResult = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
                (frame, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "agui.event",
                    requestId,
                    payload = frame,
                }, token)),
                onStartedAsync: (started, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.ack",
                    requestId,
                    payload = new
                    {
                        runId = started.RunId,
                        threadId = started.ActorId,
                        workflow = started.WorkflowName,
                    },
                }, token)),
                ct);

            if (executionResult.Error != WorkflowChatRunStartError.None)
            {
                var (code, message) = ToCommandError(executionResult.Error);
                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.error",
                    requestId,
                    code,
                    message,
                }, ct);
                return;
            }

            var started = executionResult.Started!;
            var finalize = executionResult.FinalizeResult;
            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "query.result",
                requestId,
                payload = new
                {
                    runId = started.RunId,
                    projectionCompletionStatus = finalize?.ProjectionCompletionStatus.ToString(),
                    projectionCompleted = finalize?.ProjectionCompleted ?? false,
                    report = finalize?.Report,
                },
            }, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to execute websocket chat command");
            if (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.error",
                    code = "RUN_EXECUTION_FAILED",
                    message = "Failed to execute run.",
                }, ct);
            }
        }
        finally
        {
            await ChatWebSocketProtocol.CloseAsync(socket, ct);
        }
    }

    private static async ValueTask WriteSseFrameAsync(HttpResponse response, WorkflowOutputFrame frame, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(frame, OutputJsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\\n\\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static int ToHttpStatusCode(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.WorkflowNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.AgentTypeNotSupported => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.ProjectionDisabled => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    private static (string Code, string Message) ToCommandError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => ("AGENT_TYPE_NOT_SUPPORTED", "Agent is not WorkflowGAgent."),
            WorkflowChatRunStartError.ProjectionDisabled => ("PROJECTION_DISABLED", "Projection pipeline is disabled."),
            _ => ("RUN_START_FAILED", "Failed to resolve actor."),
        };
    }
}

public sealed record ChatWsCommand
{
    public string Type { get; init; } = "chat.command";
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
