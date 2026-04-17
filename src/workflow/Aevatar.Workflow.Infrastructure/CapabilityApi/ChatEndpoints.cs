using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityEndpoints
{
    private const string WorkflowRuntimeDefaultsSectionName = "WorkflowRuntimeDefaults";

    public static IEndpointRouteBuilder MapWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");
        ChatQueryEndpoints.Map(group);

        return app;
    }

    public static IEndpointRouteBuilder MapWorkflowChatInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        return app;
    }

    public static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var writer = new ChatSseResponseWriter(http.Response);
        var serviceProvider = http.Features.Get<IServiceProvidersFeature>()?.RequestServices;
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat");

        try
        {
            var capabilities = TryResolveCapabilities(serviceProvider, logger);
            var defaultMetadata = TryResolveRuntimeDefaultMetadata(serviceProvider, logger);
            var normalizedRequest = ChatRunRequestNormalizer.Normalize(
                input,
                capabilities,
                defaultMetadata);
            if (!normalizedRequest.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            var result = await chatRunService.ExecuteAsync(
                normalizedRequest.Request!,
                async (frame, token) =>
                {
                    await writer.WriteAsync(frame, token);
                    scope.RecordFirstResponse();
                },
                onAcceptedAsync: async (receipt, token) =>
                {
                    CapabilityTraceContext.ApplyCorrelationHeader(http.Response, receipt.CorrelationId);
                    await writer.StartAsync(token);
                    await writer.WriteAsync(BuildRunContextFrame(receipt), token);
                    scope.RecordFirstResponse();
                },
                ct);

            if (!result.Succeeded && !writer.Started)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(result.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            scope.MarkError();
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
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.Command");

        var normalizedRequest = ChatRunRequestNormalizer.Normalize(input, defaultMetadata: defaultMetadata);
        if (!normalizedRequest.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
            var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
            scope.MarkResult(statusCode);
            return Results.Json(new { code, message }, statusCode: statusCode);
        }

        try
        {
            var dispatchResult = await chatRunService.DispatchAsync(
                normalizedRequest.Request!,
                ct);

            if (!dispatchResult.Succeeded || dispatchResult.Receipt == null)
            {
                var mappedError = ChatRunStartErrorMapper.ToCommandError(dispatchResult.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(dispatchResult.Error);
                scope.MarkResult(statusCode);
                return Results.Json(
                    new { code = mappedError.Code, message = mappedError.Message },
                    statusCode: statusCode);
            }

            return Results.Accepted(
                $"/api/actors/{dispatchResult.Receipt.ActorId}",
                CapabilityTraceContext.CreateAcceptedPayload(dispatchResult.Receipt));
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            scope.MarkError();
            logger.LogError(ex, "Workflow command execution failed before start signal");
            return Results.Json(
                new { code = "EXECUTION_FAILED", message = "Command execution failed." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public static async Task<IResult> HandleResume(
        WorkflowResumeInput input,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resumeService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var stepId = (input.StepId ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(stepId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId, runId and stepId are required." });
            }

            var dispatch = await resumeService.DispatchAsync(
                new WorkflowResumeCommand(
                    actorId,
                    runId,
                    stepId,
                    commandId,
                    input.Approved,
                    input.UserInput,
                    NormalizeMetadata(input.Metadata),
                    input.EditedContent,
                    input.Feedback),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            return Results.Ok(new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                stepId,
                commandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleSignal(
        WorkflowSignalInput input,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(signalService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var signalName = (input.SignalName ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            var stepId = NormalizeOptional(input.StepId);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(signalName))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId, runId and signalName are required." });
            }

            var dispatch = await signalService.DispatchAsync(
                new WorkflowSignalCommand(
                    actorId,
                    runId,
                    signalName,
                    commandId,
                    input.Payload,
                    stepId),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            return Results.Ok(new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                signalName,
                stepId,
                commandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleStop(
        WorkflowStopInput input,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stopService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            var reason = NormalizeOptional(input.Reason);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId and runId are required." });
            }

            var dispatch = await stopService.DispatchAsync(
                new WorkflowStopCommand(
                    actorId,
                    runId,
                    commandId,
                    reason),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            return Results.Ok(new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                reason,
                commandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    private static WorkflowRunEventEnvelope BuildRunContextFrame(WorkflowChatRunAcceptedReceipt receipt) =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.run.context",
                Payload = Any.Pack(new WorkflowRunContextPayload
                {
                    ActorId = receipt.ActorId,
                    WorkflowName = receipt.WorkflowName,
                    CommandId = receipt.CommandId,
                }),
            },
        };

    private static IResult MapRunControlDispatchFailure(
        WorkflowRunControlStartError error,
        ApiRequestScope scope)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowRunControlStartErrorCode.InvalidActorId => (
                StatusCodes.Status400BadRequest,
                "actorId is required."),
            WorkflowRunControlStartErrorCode.InvalidRunId => (
                StatusCodes.Status400BadRequest,
                "runId is required."),
            WorkflowRunControlStartErrorCode.InvalidStepId => (
                StatusCodes.Status400BadRequest,
                "stepId is required."),
            WorkflowRunControlStartErrorCode.InvalidSignalName => (
                StatusCodes.Status400BadRequest,
                "signalName is required."),
            WorkflowRunControlStartErrorCode.ActorNotFound => (
                StatusCodes.Status404NotFound,
                $"Actor '{error.ActorId}' not found."),
            WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => (
                StatusCodes.Status400BadRequest,
                $"Actor '{error.ActorId}' is not a workflow run actor."),
            WorkflowRunControlStartErrorCode.RunBindingMissing => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' does not have a bound run id."),
            WorkflowRunControlStartErrorCode.RunBindingMismatch => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Workflow control dispatch failed."),
        };
        scope.MarkResult(statusCode);
        return Results.Json(new { error = message }, statusCode: statusCode);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey == null || normalizedValue == null)
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

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
                new WorkflowRunEventEnvelope
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RunError = new WorkflowRunErrorEventPayload
                    {
                        Code = "EXECUTION_FAILED",
                        Message = $"Workflow execution failed: {SanitizeErrorMessage(ex.Message)}",
                    },
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
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginWebSocket();
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
                scope.RecordFirstResponse();
                return;
            }

            responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
            var capabilities = TryResolveCapabilities(http.RequestServices, logger);
            var defaultMetadata = TryResolveRuntimeDefaultMetadata(http.RequestServices, logger);
            await ChatWebSocketRunCoordinator.ExecuteAsync(
                socket,
                command,
                chatRunService,
                scope,
                ct,
                capabilities,
                defaultMetadata);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            scope.MarkError();
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

    private static WorkflowCapabilitiesDocument? TryResolveCapabilities(IServiceProvider? serviceProvider, ILogger? logger)
    {
        if (serviceProvider == null)
            return null;

        try
        {
            var queryService = serviceProvider.GetService(typeof(IWorkflowExecutionQueryApplicationService))
                               as IWorkflowExecutionQueryApplicationService;
            return queryService?.GetCapabilities();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to resolve capabilities for workflow authoring prompt augmentation.");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> TryResolveRuntimeDefaultMetadata(
        IServiceProvider? serviceProvider,
        ILogger? logger)
    {
        if (serviceProvider == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var configuration = serviceProvider.GetService(typeof(IConfiguration)) as IConfiguration;
            return ParseRuntimeDefaultMetadata(configuration);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to resolve workflow runtime default metadata from configuration.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseRuntimeDefaultMetadata(IConfiguration? configuration)
    {
        if (configuration == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var section = configuration.GetSection(WorkflowRuntimeDefaultsSectionName);
        if (!section.Exists())
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in section.AsEnumerable(makePathsRelative: true))
        {
            var normalizedKey = NormalizeRuntimeDefaultKey(rawKey);
            var normalizedValue = string.IsNullOrWhiteSpace(rawValue) ? string.Empty : rawValue.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            metadata[normalizedKey] = normalizedValue;
        }

        return metadata;
    }

    private static string NormalizeRuntimeDefaultKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        return rawKey
            .Trim()
            .Replace(':', '.');
    }

}
