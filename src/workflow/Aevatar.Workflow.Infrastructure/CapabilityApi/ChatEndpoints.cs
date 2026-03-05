using System.Net.WebSockets;
using System.Diagnostics;
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
        var requestStopwatch = Stopwatch.StartNew();
        var requestResult = ApiMetrics.ResultOk;
        var firstResponseRecorded = false;
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            requestStopwatch.Stop();
            ApiMetrics.RecordRequest(ApiMetrics.TransportHttp, requestResult, requestStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        var writer = new ChatSseResponseWriter(http.Response);
        CapabilityTraceContext.ApplyTraceHeaders(http.Response);

        try
        {
            var result = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId, input.WorkflowYaml),
                (frame, token) =>
                {
                    if (!firstResponseRecorded)
                    {
                        firstResponseRecorded = true;
                        ApiMetrics.RecordFirstResponse(ApiMetrics.TransportHttp, ApiMetrics.ResultOk, requestStopwatch.Elapsed.TotalMilliseconds);
                    }
                    return writer.WriteAsync(frame, token);
                },
                onStartedAsync: (started, token) =>
                {
                    CapabilityTraceContext.ApplyCorrelationHeader(http.Response, started.CommandId);
                    return writer.StartAsync(token);
                },
                ct);

            if (result.Error != WorkflowChatRunStartError.None && !writer.Started)
            {
                http.Response.StatusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
                requestResult = ApiMetrics.ResolveResult(http.Response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            requestResult = ApiMetrics.ResultError;
            throw;
        }
        finally
        {
            requestStopwatch.Stop();
            ApiMetrics.RecordRequest(ApiMetrics.TransportHttp, requestResult, requestStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    internal static async Task<IResult> HandleCommand(
        ChatInput input,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var requestResult = ApiMetrics.ResultOk;
        try
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
            Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> executionTask;

            try
            {
                executionTask = chatRunService.ExecuteAsync(
                    new WorkflowChatRunRequest(input.Prompt, input.Workflow, input.AgentId, input.WorkflowYaml),
                    static (_, _) => ValueTask.CompletedTask,
                    onStartedAsync: (started, _) =>
                    {
                        startSignal.TrySetResult(started);
                        return ValueTask.CompletedTask;
                    },
                    ct);
            }
            catch (Exception ex)
            {
                requestResult = ApiMetrics.ResultError;
                logger.LogError(ex, "Workflow command execution failed before start signal");
                return Results.Json(
                    new { code = "EXECUTION_FAILED", message = "Command execution failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

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
                    CapabilityTraceContext.CreateAcceptedPayload(started));
            }

            CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> result;
            try
            {
                result = await executionTask;
            }
            catch (Exception ex)
            {
                requestResult = ApiMetrics.ResultError;
                logger.LogError(ex, "Workflow command execution failed before start signal");
                return Results.Json(
                    new { code = "EXECUTION_FAILED", message = "Command execution failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (result.Error != WorkflowChatRunStartError.None)
            {
                var mappedError = ChatRunStartErrorMapper.ToCommandError(result.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
                requestResult = ApiMetrics.ResolveResult(statusCode);
                return Results.Json(
                    new
                    {
                        code = mappedError.Code,
                        message = mappedError.Message,
                    },
                    statusCode: statusCode);
            }

            if (result.Started != null)
            {
                return Results.Accepted(
                    $"/api/actors/{result.Started.ActorId}",
                    CapabilityTraceContext.CreateAcceptedPayload(result.Started));
            }

            requestResult = ApiMetrics.ResultError;
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            requestStopwatch.Stop();
            ApiMetrics.RecordRequest(ApiMetrics.TransportHttp, requestResult, requestStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    internal static async Task HandleChatWebSocket(
        HttpContext http,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var requestResult = ApiMetrics.ResultOk;
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected websocket request.", ct);
            requestStopwatch.Stop();
            ApiMetrics.RecordRequest(ApiMetrics.TransportWebSocket, requestResult, requestStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat.WebSocket");
        var responseMessageType = WebSocketMessageType.Text;

        try
        {
            var incomingFrame = await ChatWebSocketProtocol.ReceiveAsync(socket, ct);
            responseMessageType = incomingFrame.HasValue
                ? ChatWebSocketProtocol.NormalizeMessageType(incomingFrame.Value.MessageType)
                : WebSocketMessageType.Text;

            if (!ChatWebSocketCommandParser.TryParse(incomingFrame, out var command, out var parseError))
            {
                var parseContext = CapabilityTraceContext.CreateMessageContext(fallbackCorrelationId: parseError.RequestId ?? string.Empty);
                await ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandError(
                        parseError.RequestId,
                        parseError.Code,
                        parseError.Message,
                        parseContext.CorrelationId,
                        parseContext.TraceId),
                    ct,
                    parseError.ResponseMessageType);
                ApiMetrics.RecordFirstResponse(ApiMetrics.TransportWebSocket, ApiMetrics.ResultOk, requestStopwatch.Elapsed.TotalMilliseconds);
                return;
            }

            responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
            var firstResponseDurationMs = await ChatWebSocketRunCoordinator.ExecuteAsync(socket, command, chatRunService, ct);
            if (firstResponseDurationMs.HasValue)
                ApiMetrics.RecordFirstResponse(ApiMetrics.TransportWebSocket, ApiMetrics.ResultOk, firstResponseDurationMs.Value);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            requestResult = ApiMetrics.ResultError;
            logger?.LogWarning(ex, "Failed to execute websocket chat command");
            if (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var failureContext = CapabilityTraceContext.CreateMessageContext();
                await ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandError(
                        requestId: null,
                        code: "RUN_EXECUTION_FAILED",
                        message: "Failed to execute run.",
                        traceId: failureContext.TraceId),
                    ct,
                    responseMessageType);
            }
        }
        finally
        {
            await ChatWebSocketProtocol.CloseAsync(socket, ct);
            requestStopwatch.Stop();
            ApiMetrics.RecordRequest(ApiMetrics.TransportWebSocket, requestResult, requestStopwatch.Elapsed.TotalMilliseconds);
        }
    }

}
