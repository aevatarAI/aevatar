using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");
        MapInteractionEndpoints(group);
        ChatQueryEndpoints.Map(group);

        return app;
    }

    public static IEndpointRouteBuilder MapWorkflowChatInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");
        MapInteractionEndpoints(group);

        return app;
    }

    private static void MapInteractionEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/chat", HandleChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/ws/chat", HandleChatWebSocket);
        group.MapPost("/workflows/resume", HandleResume)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/workflows/signal", HandleSignal)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
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
        var serviceProvider = http.Features.Get<IServiceProvidersFeature>()?.RequestServices;
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat");

        try
        {
            var normalizedRequest = ChatRunRequestNormalizer.Normalize(input);
            if (!normalizedRequest.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
                await WriteJsonErrorResponseAsync(
                    http,
                    ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error),
                    code,
                    message,
                    ct);
                return;
            }

            var result = await chatRunService.ExecuteAsync(
                normalizedRequest.Request!,
                (frame, token) => writer.WriteAsync(frame, token),
                onStartedAsync: async (started, token) =>
                {
                    CapabilityTraceContext.ApplyCorrelationHeader(http.Response, started.CommandId);
                    await writer.StartAsync(token);
                    await writer.WriteAsync(BuildRunContextFrame(started), token);
                },
                ct);

            if (result.Error != WorkflowChatRunStartError.None && !writer.Started)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(result.Error);
                await WriteJsonErrorResponseAsync(
                    http,
                    ChatRunStartErrorMapper.ToHttpStatusCode(result.Error),
                    code,
                    message,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Workflow chat execution failed.");
            if (!writer.Started)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status500InternalServerError,
                    "EXECUTION_FAILED",
                    "Workflow execution failed.",
                    CancellationToken.None);
                return;
            }

            await WriteStreamErrorFrameAsync(writer, ex, logger, CancellationToken.None);
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
        Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> executionTask;

        try
        {
            var normalizedRequest = ChatRunRequestNormalizer.Normalize(input);
            if (!normalizedRequest.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
                return Results.Json(
                    new
                    {
                        code,
                        message,
                    },
                    statusCode: ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error));
            }

            executionTask = chatRunService.ExecuteAsync(
                normalizedRequest.Request!,
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
                $"/api/actors/{started.RunActorId}",
                CapabilityTraceContext.CreateAcceptedPayload(started));
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
            var mappedError = ChatRunStartErrorMapper.ToCommandError(result.Error);
            return Results.Json(
                new
                {
                    code = mappedError.Code,
                    message = mappedError.Message,
                },
                statusCode: ChatRunStartErrorMapper.ToHttpStatusCode(result.Error));
        }

        if (result.Started != null)
        {
            return Results.Accepted(
                $"/api/actors/{result.Started.RunActorId}",
                CapabilityTraceContext.CreateAcceptedPayload(result.Started));
        }

        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    internal static async Task<IResult> HandleResume(
        WorkflowResumeInput input,
        IWorkflowRunActorPort actorPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actorPort);

        var runActorId = (input.RunActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var resumeToken = (input.ResumeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runActorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(resumeToken))
        {
            return Results.BadRequest(new { error = "runActorId, runId and resumeToken are required." });
        }

        var actor = await actorPort.GetRunActorAsync(runActorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Run actor '{runActorId}' not found." });

        if (!await actorPort.IsWorkflowRunActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{runActorId}' is not a workflow run actor." });

        var resumed = new WorkflowResumedEvent
        {
            RunId = runId,
            Approved = input.Approved,
            UserInput = input.UserInput ?? string.Empty,
            ResumeToken = resumeToken,
        };
        if (input.Metadata is { Count: > 0 })
        {
            foreach (var (key, value) in input.Metadata)
                resumed.Metadata[key] = value;
        }
        var commandId = (input.CommandId ?? string.Empty).Trim();
        var correlationId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(resumed),
            PublisherId = "api.workflow.resume",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            runActorId,
            runId,
            resumeToken,
            commandId = correlationId,
        });
    }

    internal static async Task<IResult> HandleSignal(
        WorkflowSignalInput input,
        IWorkflowRunActorPort actorPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actorPort);

        var runActorId = (input.RunActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var waitToken = (input.WaitToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runActorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(waitToken))
        {
            return Results.BadRequest(new { error = "runActorId, runId and waitToken are required." });
        }

        var actor = await actorPort.GetRunActorAsync(runActorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Run actor '{runActorId}' not found." });

        if (!await actorPort.IsWorkflowRunActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{runActorId}' is not a workflow run actor." });

        var commandId = (input.CommandId ?? string.Empty).Trim();
        var correlationId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new SignalReceivedEvent
            {
                RunId = runId,
                Payload = input.Payload ?? string.Empty,
                WaitToken = waitToken,
            }),
            PublisherId = "api.workflow.signal",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            runActorId,
            runId,
            waitToken,
            commandId = correlationId,
        });
    }

    private static WorkflowOutputFrame BuildRunContextFrame(WorkflowChatRunStarted started) =>
        new()
        {
            Type = WorkflowRunEventTypes.Custom,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Name = "aevatar.run.context",
            Value = new
            {
                started.RunActorId,
                started.DefinitionActorId,
                started.WorkflowName,
                started.CommandId,
            },
        };

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(
            new
            {
                code,
                message,
            },
            cancellationToken: ct);
    }

    private static async Task WriteStreamErrorFrameAsync(
        ChatSseResponseWriter writer,
        Exception ex,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(
                new WorkflowOutputFrame
                {
                    Type = WorkflowRunEventTypes.RunError,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Code = "EXECUTION_FAILED",
                    Message = $"Workflow execution failed: {SanitizeErrorMessage(ex.Message)}",
                },
                ct);
        }
        catch (Exception writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
        }
    }

    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "unknown error";

        return message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
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
                        parseContext.CorrelationId),
                    ct,
                    parseError.ResponseMessageType);
                return;
            }

            responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
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
                var failureContext = CapabilityTraceContext.CreateMessageContext();
                await ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandError(
                        requestId: null,
                        code: "RUN_EXECUTION_FAILED",
                        message: "Failed to execute run.",
                        correlationId: failureContext.CorrelationId),
                    ct,
                    responseMessageType);
            }
        }
        finally
        {
            await ChatWebSocketProtocol.CloseAsync(socket, ct);
        }
    }

}
