using Aevatar.CQRS.Core.Abstractions.Commands;
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
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        CancellationToken ct = default)
    {
        var writer = new ChatSseResponseWriter(http.Response);

        try
        {
            var result = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
                (frame, token) => writer.WriteAsync(frame, token),
                onStartedAsync: (_, token) => writer.StartAsync(token),
                ct);

            if (result.Error != WorkflowChatRunStartError.None && !writer.Started)
                http.Response.StatusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static async Task HandleChatWebSocket(
        HttpContext http,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
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
            if (!ChatWebSocketCommandParser.TryParse(commandText, out var command, out var parseError))
            {
                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.error",
                    requestId = parseError.RequestId,
                    code = parseError.Code,
                    message = parseError.Message,
                }, ct);
                return;
            }

            await ChatWebSocketRunCoordinator.ExecuteAsync(socket, command, chatRunService, ct);
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
}

public sealed record ChatWsCommand
{
    public string Type { get; init; } = "chat.command";
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
