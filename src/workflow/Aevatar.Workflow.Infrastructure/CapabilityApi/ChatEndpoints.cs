using System.Net.WebSockets;
using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Bridge;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        group.MapPost("/bridge/callback-token", HandleBridgeCallbackTokenIssue)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/bridge/callbacks", HandleBridgeIngress)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
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
            var capabilities = TryResolveCapabilities(serviceProvider, logger);
            var normalizedRequest = ChatRunRequestNormalizer.Normalize(
                input,
                capabilities);
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
                $"/api/actors/{result.Started.ActorId}",
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

        var actorId = (input.ActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var stepId = (input.StepId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(stepId))
        {
            return Results.BadRequest(new { error = "actorId, runId and stepId are required." });
        }

        var actor = await actorPort.GetAsync(actorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Actor '{actorId}' not found." });

        if (!await actorPort.IsWorkflowActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{actorId}' is not a workflow actor." });

        var resumed = new WorkflowResumedEvent
        {
            RunId = runId,
            StepId = stepId,
            Approved = input.Approved,
            UserInput = input.UserInput ?? string.Empty,
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
            actorId,
            runId,
            stepId,
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

        var actorId = (input.ActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var signalName = (input.SignalName ?? string.Empty).Trim();
        var stepId = (input.StepId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(signalName))
        {
            return Results.BadRequest(new { error = "actorId, runId and signalName are required." });
        }

        var actor = await actorPort.GetAsync(actorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Actor '{actorId}' not found." });

        if (!await actorPort.IsWorkflowActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{actorId}' is not a workflow actor." });

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
                StepId = stepId,
                SignalName = signalName,
                Payload = input.Payload ?? string.Empty,
            }),
            PublisherId = "api.workflow.signal",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            actorId,
            runId,
            signalName,
            stepId,
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
                started.ActorId,
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
            var capabilities = TryResolveCapabilities(http.RequestServices, logger);
            await ChatWebSocketRunCoordinator.ExecuteAsync(
                socket,
                command,
                chatRunService,
                ct,
                capabilities);
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

    internal static IResult HandleBridgeCallbackTokenIssue(
        [FromBody] BridgeCallbackTokenIssueInput input,
        [FromServices] IBridgeCallbackTokenService tokenService,
        [FromServices] IOptions<WorkflowBridgeOptions> bridgeOptions)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(bridgeOptions);

        var actorId = (input.ActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var stepId = (input.StepId ?? string.Empty).Trim();
        var signalName = (input.SignalName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(stepId) ||
            string.IsNullOrWhiteSpace(signalName))
        {
            return Results.BadRequest(new { error = "actorId, runId, stepId and signalName are required." });
        }

        var options = bridgeOptions.Value;
        var timeoutMs = input.TimeoutMs ?? options.DefaultTokenTtlMs;
        if (timeoutMs <= 0)
            return Results.BadRequest(new { error = "timeoutMs must be positive." });

        var maxTokenTtlMs = Math.Clamp(options.MaxTokenTtlMs, 1_000, 86_400_000);
        timeoutMs = Math.Clamp(timeoutMs, 1_000, maxTokenTtlMs);
        var issueResult = tokenService.Issue(
            new BridgeCallbackTokenIssueRequest
            {
                ActorId = actorId,
                RunId = runId,
                StepId = stepId,
                SignalName = signalName,
                TimeoutMs = timeoutMs,
                ChannelId = (input.ChannelId ?? string.Empty).Trim(),
                SessionId = (input.SessionId ?? string.Empty).Trim(),
                Metadata = NormalizeMetadata(input.Metadata),
            },
            DateTimeOffset.UtcNow);

        return Results.Ok(new
        {
            token = issueResult.Token,
            tokenId = issueResult.TokenId,
            bridgeActorId = options.BridgeActorId,
            actorId = issueResult.Claims.ActorId,
            runId = issueResult.Claims.RunId,
            stepId = issueResult.Claims.StepId,
            signalName = issueResult.Claims.SignalName,
            issuedAtUnixTimeMs = issueResult.Claims.IssuedAtUnixTimeMs,
            expiresAtUnixTimeMs = issueResult.Claims.ExpiresAtUnixTimeMs,
            nonce = issueResult.Claims.Nonce,
            channelId = issueResult.Claims.ChannelId,
            sessionId = issueResult.Claims.SessionId,
        });
    }

    internal static async Task<IResult> HandleBridgeIngress(
        [FromBody] BridgeIngressInput input,
        [FromServices] IActorRuntime runtime,
        [FromServices] IOptions<WorkflowBridgeOptions> bridgeOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(bridgeOptions);

        var source = (input.Source ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input.CallbackToken) || string.IsNullOrWhiteSpace(source))
            return Results.BadRequest(new { error = "callbackToken and source are required." });

        var options = bridgeOptions.Value;
        if (options.RequireSourceAllowList &&
            options.AllowedSources.Count > 0 &&
            !options.AllowedSources.Any(allowed => string.Equals(allowed, source, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.BadRequest(new { error = $"source '{source}' is not allowed." });
        }

        var bridgeActor = await GetOrCreateBridgeActorAsync(
            runtime,
            options.BridgeActorId,
            options.BridgeAgentType,
            ct);
        var commandId = (input.CommandId ?? string.Empty).Trim();
        var correlationId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await bridgeActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BridgeInboundCallbackReceivedEvent
            {
                CallbackToken = input.CallbackToken.Trim(),
                Payload = input.Payload ?? string.Empty,
                Source = source,
                SourceMessageId = (input.SourceMessageId ?? string.Empty).Trim(),
                SourceChatId = (input.SourceChatId ?? string.Empty).Trim(),
                SourceUserId = (input.SourceUserId ?? string.Empty).Trim(),
                ReceivedAtUnixTimeMs = input.ReceivedAtUnixTimeMs.GetValueOrDefault(nowMs),
            }),
            PublisherId = "api.bridge.ingress",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = bridgeActor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            commandId = correlationId,
            bridgeActorId = bridgeActor.Id,
        });
    }

    private static async Task<IActor> GetOrCreateBridgeActorAsync(
        IActorRuntime runtime,
        string bridgeActorId,
        string bridgeAgentType,
        CancellationToken ct)
    {
        var normalizedActorId = string.IsNullOrWhiteSpace(bridgeActorId)
            ? "bridge:default"
            : bridgeActorId.Trim();
        var actor = await runtime.GetAsync(normalizedActorId);
        if (actor != null)
            return actor;

        var resolvedAgentType = ResolveBridgeActorType(bridgeAgentType);
        return await runtime.CreateAsync(resolvedAgentType, normalizedActorId, ct);
    }

    private static System.Type ResolveBridgeActorType(string configuredType)
    {
        if (string.IsNullOrWhiteSpace(configuredType))
            return typeof(BridgeGAgent);

        var normalized = configuredType.Trim();
        var resolved = System.Type.GetType(normalized, throwOnError: false, ignoreCase: true);
        if (resolved == null)
        {
            var matches = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(static assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        return ex.Types.OfType<System.Type>();
                    }
                })
                .Where(type =>
                    string.Equals(type.FullName, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, normalized, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            if (matches.Length == 1)
                resolved = matches[0];
            else if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"Configured bridge agent type '{normalized}' is ambiguous. Use assembly-qualified name.");
        }

        if (resolved == null)
            throw new InvalidOperationException($"Configured bridge agent type '{normalized}' was not found.");
        if (!typeof(BridgeGAgent).IsAssignableFrom(resolved))
        {
            throw new InvalidOperationException(
                $"Configured bridge agent type '{resolved.FullName}' must inherit {nameof(BridgeGAgent)}.");
        }

        return resolved;
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }
}
