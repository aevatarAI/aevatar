using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
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
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

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

    internal static async Task<IResult> HandleCommand(
        ChatInput input,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_PROMPT",
                message = "Prompt is required.",
            });
        }

        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.Command");
        var startSignal = new TaskCompletionSource<WorkflowChatRunStarted>(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionTask = Task.Run(
            () => chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId),
                static (_, _) => ValueTask.CompletedTask,
                onStartedAsync: (started, _) =>
                {
                    startSignal.TrySetResult(started);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None),
            CancellationToken.None);

        var completed = await Task.WhenAny(startSignal.Task, executionTask);

        if (completed == startSignal.Task)
        {
            var started = await startSignal.Task;
            _ = executionTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        logger.LogWarning(t.Exception, "Background workflow command failed. commandId={CommandId}", started.CommandId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return Results.Accepted(
                $"/api/actors/{started.ActorId}",
                new
                {
                    commandId = started.CommandId,
                    actorId = started.ActorId,
                });
        }

        CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> result;
        try
        {
            result = await executionTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow command execution failed before start signal");
            return Results.Json(
                new { code = "EXECUTION_FAILED", message = "Command execution failed." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (result.Error != WorkflowChatRunStartError.None)
        {
            return Results.Json(
                new
                {
                    code = ChatRunStartErrorMapper.ToCommandError(result.Error).Code,
                    message = ChatRunStartErrorMapper.ToCommandError(result.Error).Message,
                },
                statusCode: ChatRunStartErrorMapper.ToHttpStatusCode(result.Error));
        }

        if (result.Started != null)
        {
            return Results.Accepted(
                $"/api/actors/{result.Started.ActorId}",
                new
                {
                    commandId = result.Started.CommandId,
                    actorId = result.Started.ActorId,
                });
        }

        return Results.StatusCode(StatusCodes.Status500InternalServerError);
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
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat.WebSocket");
        var responseMessageType = WebSocketMessageType.Text;

        try
        {
            var incomingFrame = await ChatWebSocketProtocol.ReceiveAsync(socket, ct);
            if (incomingFrame != null && incomingFrame.Value.MessageType == WebSocketMessageType.Binary)
                responseMessageType = WebSocketMessageType.Binary;

            if (!ChatWebSocketCommandParser.TryParse(incomingFrame, out var command, out var parseError))
            {
                await ChatWebSocketProtocol.SendAsync(socket, new
                {
                    type = "command.error",
                    requestId = parseError.RequestId,
                    code = parseError.Code,
                    message = parseError.Message,
                }, ct, responseMessageType);
                return;
            }

            responseMessageType = command.ResponseMessageType == WebSocketMessageType.Binary
                ? WebSocketMessageType.Binary
                : WebSocketMessageType.Text;
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
                }, ct, responseMessageType);
            }
        }
        finally
        {
            await ChatWebSocketProtocol.CloseAsync(socket, ct);
        }
    }
}
