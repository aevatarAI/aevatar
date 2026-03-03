using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class OpenClawBridgeOptions
{
    public bool Enabled { get; set; } = true;
    public bool RequireAuthToken { get; set; } = false;
    public string AuthHeaderName { get; set; } = "X-OpenClaw-Bridge-Token";
    public string AuthToken { get; set; } = "";
    public string DefaultWorkflow { get; set; } = "68_claw_channel_entry";
    public int CallbackTimeoutMs { get; set; } = 5000;
    public string CallbackAuthHeaderName { get; set; } = "Authorization";
    public string CallbackAuthScheme { get; set; } = "Bearer";
    public int CallbackMaxAttempts { get; set; } = 1;
    public int CallbackRetryDelayMs { get; set; } = 300;
    public List<string> CallbackAllowedHosts { get; set; } = [];
    public bool EnableIdempotency { get; set; } = true;
    public int IdempotencyTtlHours { get; set; } = 24;
}

public sealed record OpenClawAgentHookInput
{
    public string? Prompt { get; init; }
    public string? Message { get; init; }
    public string? Text { get; init; }
    public string? Workflow { get; init; }
    public string? ActorId { get; init; }
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public string? MessageId { get; init; }
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<string>? WorkflowYamls { get; init; }
    public string? CallbackUrl { get; init; }
    public string? CallbackToken { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

internal static class OpenClawBridgeEndpoints
{
    internal const string ReceiptClientName = "openclaw.bridge.receipt";
    private static readonly Regex ContextTokenRegex =
        new("^[A-Za-z0-9._:@#/\\-]+$", RegexOptions.Compiled);

    internal static async Task<IResult> HandleOpenClawAgentHook(
        HttpContext http,
        OpenClawAgentHookInput input,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        [FromServices] IFileBackedWorkflowNameCatalog fileBackedWorkflowNames,
        ILoggerFactory loggerFactory,
        [FromServices] IOptions<OpenClawBridgeOptions>? bridgeOptionsAccessor = null,
        [FromServices] IHttpClientFactory? httpClientFactory = null,
        [FromServices] IOpenClawIdempotencyStore? idempotencyStore = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.OpenClawBridge");
        var options = bridgeOptionsAccessor?.Value ?? new OpenClawBridgeOptions();
        if (!options.Enabled)
        {
            return Results.NotFound(new
            {
                code = "OPENCLAW_BRIDGE_DISABLED",
                message = "OpenClaw bridge endpoint is disabled.",
            });
        }

        if (!IsAuthorized(http, options, out var authError))
        {
            return Results.Json(
                new
                {
                    code = "UNAUTHORIZED",
                    message = authError,
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var prompt = ResolvePrompt(input);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_PROMPT",
                message = "prompt/message/text is required.",
            });
        }

        if (!TryNormalizeBridgeInput(input, out var normalizedInput, out var normalizeError))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_CONTEXT",
                message = normalizeError,
            });
        }

        var workflow = string.IsNullOrWhiteSpace(input.Workflow)
            ? options.DefaultWorkflow
            : input.Workflow.Trim();
        var sessionKey = ResolveSessionKey(
            normalizedInput.SessionId,
            normalizedInput.ChannelId,
            normalizedInput.UserId,
            normalizedInput.MessageId);
        var idempotencyKey = ResolveIdempotencyKey(
            normalizedInput.IdempotencyKey,
            normalizedInput.ChannelId,
            normalizedInput.UserId,
            normalizedInput.MessageId,
            sessionKey);
        var correlationId = ResolveCorrelationId(normalizedInput.MessageId, idempotencyKey);
        var actorId = ResolveActorId(normalizedInput.ActorId, sessionKey);
        var metadata = CloneMetadata(input.Metadata);
        metadata["session_id"] = sessionKey;
        metadata["channel_id"] = normalizedInput.ChannelId;
        metadata["user_id"] = normalizedInput.UserId;
        metadata["message_id"] = normalizedInput.MessageId;
        metadata["correlation_id"] = correlationId;
        metadata["idempotency_key"] = idempotencyKey;

        var bridgeContext = new OpenClawBridgeContext(
            SessionKey: sessionKey,
            IdempotencyKey: idempotencyKey,
            CorrelationId: correlationId,
            ChannelId: normalizedInput.ChannelId,
            UserId: normalizedInput.UserId,
            MessageId: normalizedInput.MessageId,
            CallbackUrl: normalizedInput.CallbackUrl,
            CallbackToken: normalizedInput.CallbackToken,
            WorkflowName: workflow,
            Metadata: metadata);
        var receiptState = new BridgeReceiptState();
        var idempotencyAcquired = false;

        if (options.EnableIdempotency &&
            idempotencyStore != null &&
            !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var acquire = await idempotencyStore.AcquireAsync(
                new OpenClawIdempotencyAcquireRequest(
                    IdempotencyKey: idempotencyKey,
                    SessionKey: sessionKey,
                    CorrelationId: correlationId,
                    ActorId: actorId,
                    WorkflowName: workflow,
                    ChannelId: bridgeContext.ChannelId,
                    UserId: bridgeContext.UserId,
                    MessageId: bridgeContext.MessageId,
                    TtlHours: options.IdempotencyTtlHours),
                ct);

            if (acquire.Status != OpenClawIdempotencyAcquireStatus.Acquired)
            {
                return BuildDuplicateRequestResult(acquire, bridgeContext);
            }

            idempotencyAcquired = true;
        }

        var normalized = ChatRunRequestNormalizer.Normalize(
            new ChatInput
            {
                Prompt = prompt,
                Workflow = workflow,
                AgentId = actorId,
                WorkflowYamls = input.WorkflowYamls,
                Metadata = BuildRunMetadata(bridgeContext),
            },
            fileBackedWorkflowNames);
        if (!normalized.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalized.Error);
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                bridgeContext,
                logger,
                "aevatar.workflow.rejected",
                new
                {
                    code,
                    message,
                },
                receiptState,
                ct: ct);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: code,
                errorMessage: message,
                idempotencyAcquired,
                ct);
            return Results.Json(
                new
                {
                    code,
                    message,
                    correlationId,
                    idempotencyKey,
                },
                statusCode: ChatRunStartErrorMapper.ToHttpStatusCode(normalized.Error));
        }

        var startSignal = new TaskCompletionSource<WorkflowChatRunStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> executionTask;
        try
        {
            executionTask = chatRunService.ExecuteAsync(
                normalized.Request!,
                (frame, token) => TrySendReceiptAsync(
                    httpClientFactory,
                    options,
                    bridgeContext,
                    logger,
                    "aevatar.workflow.frame",
                    new
                    {
                        frame.Type,
                        frame.Timestamp,
                        frame.ThreadId,
                        frame.StepName,
                        frame.Code,
                        frame.Message,
                        frame.Name,
                        frame.Value,
                    },
                    receiptState,
                    actorId: string.IsNullOrWhiteSpace(receiptState.ActorId) ? frame.ThreadId : receiptState.ActorId,
                    commandId: receiptState.CommandId,
                    token),
                onStartedAsync: async (started, token) =>
                {
                    receiptState.SetStarted(started.ActorId, started.CommandId);
                    startSignal.TrySetResult(started);
                    await MarkIdempotencyStartedAsync(
                        idempotencyStore,
                        idempotencyKey,
                        started.ActorId,
                        started.CommandId,
                        started.WorkflowName,
                        idempotencyAcquired,
                        token);
                    await TrySendReceiptAsync(
                        httpClientFactory,
                        options,
                        bridgeContext,
                        logger,
                        "aevatar.workflow.started",
                        new
                        {
                            started.ActorId,
                            started.CommandId,
                            started.WorkflowName,
                        },
                        receiptState,
                        actorId: started.ActorId,
                        commandId: started.CommandId,
                        token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenClaw bridge failed to start workflow execution.");
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                bridgeContext,
                logger,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Bridge failed before workflow start.",
                    error = ex.Message,
                },
                receiptState,
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Bridge failed before workflow start.",
                idempotencyAcquired,
                CancellationToken.None);
            return Results.Json(
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Bridge failed before workflow start.",
                    correlationId,
                    idempotencyKey,
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var completed = await Task.WhenAny(startSignal.Task, executionTask);
        if (completed == startSignal.Task)
        {
            var started = await startSignal.Task;
            _ = Task.Run(
                async () =>
                {
                    await ObserveExecutionCompletionAsync(
                        executionTask,
                        httpClientFactory,
                        options,
                        bridgeContext,
                        logger,
                        receiptState,
                        idempotencyStore,
                        idempotencyKey,
                        idempotencyAcquired);
                },
                CancellationToken.None);

            return Results.Accepted(
                $"/api/actors/{started.ActorId}",
                new
                {
                    accepted = true,
                    started.ActorId,
                    started.CommandId,
                    workflow = started.WorkflowName,
                    correlationId,
                    idempotencyKey,
                    sessionKey,
                    channelId = bridgeContext.ChannelId,
                    userId = bridgeContext.UserId,
                });
        }

        CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> result;
        try
        {
            result = await executionTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenClaw bridge execution task failed before start signal.");
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                bridgeContext,
                logger,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Workflow execution failed.",
                    error = ex.Message,
                },
                receiptState,
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Workflow execution failed.",
                idempotencyAcquired,
                CancellationToken.None);
            return Results.Json(
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Workflow execution failed.",
                    correlationId,
                    idempotencyKey,
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (result.Error != WorkflowChatRunStartError.None)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(result.Error);
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                bridgeContext,
                logger,
                "aevatar.workflow.rejected",
                new
                {
                    code,
                    message,
                },
                receiptState,
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: code,
                errorMessage: message,
                idempotencyAcquired,
                CancellationToken.None);
            return Results.Json(
                new
                {
                    code,
                    message,
                    correlationId,
                    idempotencyKey,
                },
                statusCode: ChatRunStartErrorMapper.ToHttpStatusCode(result.Error));
        }

        if (result.Started != null)
        {
            await MarkIdempotencyStartedAsync(
                idempotencyStore,
                idempotencyKey,
                result.Started.ActorId,
                result.Started.CommandId,
                result.Started.WorkflowName,
                idempotencyAcquired,
                CancellationToken.None);
            if (result.FinalizeResult != null)
            {
                await MarkIdempotencyCompletedAsync(
                    idempotencyStore,
                    idempotencyKey,
                    success: true,
                    errorCode: "",
                    errorMessage: "",
                    idempotencyAcquired,
                    CancellationToken.None);
            }
            return Results.Accepted(
                $"/api/actors/{result.Started.ActorId}",
                new
                {
                    accepted = true,
                    actorId = result.Started.ActorId,
                    commandId = result.Started.CommandId,
                    workflow = result.Started.WorkflowName,
                    correlationId,
                    idempotencyKey,
                    sessionKey,
                    channelId = bridgeContext.ChannelId,
                    userId = bridgeContext.UserId,
                });
        }

        await MarkIdempotencyCompletedAsync(
            idempotencyStore,
            idempotencyKey,
            success: false,
            errorCode: "EXECUTION_FAILED",
            errorMessage: "Workflow execution did not produce a start signal.",
            idempotencyAcquired,
            CancellationToken.None);

        return Results.Json(
            new
            {
                code = "EXECUTION_FAILED",
                message = "Workflow execution did not produce a start signal.",
                correlationId,
                idempotencyKey,
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task ObserveExecutionCompletionAsync(
        Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> executionTask,
        IHttpClientFactory? httpClientFactory,
        OpenClawBridgeOptions options,
        OpenClawBridgeContext context,
        ILogger logger,
        BridgeReceiptState receiptState,
        IOpenClawIdempotencyStore? idempotencyStore,
        string idempotencyKey,
        bool idempotencyAcquired)
    {
        try
        {
            var result = await executionTask.ConfigureAwait(false);
            if (result.Error == WorkflowChatRunStartError.None && result.FinalizeResult != null)
            {
                receiptState.SetStarted(result.Started?.ActorId, result.Started?.CommandId);
                await TrySendReceiptAsync(
                    httpClientFactory,
                    options,
                    context,
                    logger,
                    "aevatar.workflow.completed",
                    new
                    {
                        projectionStatus = result.FinalizeResult.ProjectionCompletionStatus.ToString(),
                        projectionCompleted = result.FinalizeResult.ProjectionCompleted,
                        actorId = result.Started?.ActorId ?? string.Empty,
                        commandId = result.Started?.CommandId ?? string.Empty,
                    },
                    receiptState,
                    actorId: result.Started?.ActorId,
                    commandId: result.Started?.CommandId,
                    CancellationToken.None);
                await MarkIdempotencyCompletedAsync(
                    idempotencyStore,
                    idempotencyKey,
                    success: true,
                    errorCode: "",
                    errorMessage: "",
                    idempotencyAcquired,
                    CancellationToken.None);
                return;
            }

            var (code, message) = ChatRunStartErrorMapper.ToCommandError(result.Error);
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                context,
                logger,
                "aevatar.workflow.failed",
                new
                {
                    code,
                    message,
                    actorId = result.Started?.ActorId ?? string.Empty,
                    commandId = result.Started?.CommandId ?? string.Empty,
                },
                receiptState,
                actorId: result.Started?.ActorId,
                commandId: result.Started?.CommandId,
                CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: code,
                errorMessage: message,
                idempotencyAcquired,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenClaw bridge failed while observing workflow completion.");
            await TrySendReceiptAsync(
                httpClientFactory,
                options,
                context,
                logger,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Workflow execution failed after start.",
                    error = ex.Message,
                },
                receiptState,
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyStore,
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Workflow execution failed after start.",
                idempotencyAcquired,
                CancellationToken.None);
        }
    }

    private static async ValueTask TrySendReceiptAsync(
        IHttpClientFactory? httpClientFactory,
        OpenClawBridgeOptions options,
        OpenClawBridgeContext context,
        ILogger logger,
        string eventType,
        object payload,
        BridgeReceiptState receiptState,
        string? actorId = null,
        string? commandId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.CallbackUrl))
            return;

        if (!Uri.TryCreate(context.CallbackUrl, UriKind.Absolute, out var callbackUri))
            return;

        if (!IsCallbackHostAllowed(callbackUri, options))
        {
            logger.LogWarning(
                "OpenClaw bridge callback host is not allowed. host={Host} event={EventType}",
                callbackUri.Host,
                eventType);
            return;
        }

        var authToken = string.IsNullOrWhiteSpace(context.CallbackToken) ? string.Empty : context.CallbackToken;
        var sequence = receiptState.NextSequence();
        var resolvedActorId = NormalizeToken(actorId);
        if (string.IsNullOrWhiteSpace(resolvedActorId))
            resolvedActorId = receiptState.ActorId;
        var resolvedCommandId = NormalizeToken(commandId);
        if (string.IsNullOrWhiteSpace(resolvedCommandId))
            resolvedCommandId = receiptState.CommandId;

        var bodyJson = JsonSerializer.Serialize(
            new
            {
                eventId = BuildEventId(context.IdempotencyKey, sequence),
                sequence,
                type = eventType,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                correlationId = context.CorrelationId,
                idempotencyKey = context.IdempotencyKey,
                sessionKey = context.SessionKey,
                channelId = context.ChannelId,
                userId = context.UserId,
                messageId = context.MessageId,
                actorId = resolvedActorId,
                commandId = resolvedCommandId,
                workflowName = context.WorkflowName,
                metadata = context.Metadata,
                payload,
            });

        var timeoutMs = Math.Clamp(options.CallbackTimeoutMs, 500, 60_000);
        var maxAttempts = Math.Clamp(options.CallbackMaxAttempts, 1, 5);
        var retryDelayMs = Math.Clamp(options.CallbackRetryDelayMs, 100, 10_000);

        var adHocClient = httpClientFactory == null ? new HttpClient() : null;
        var client = httpClientFactory?.CreateClient(ReceiptClientName) ?? adHocClient!;
        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, callbackUri);
                if (!string.IsNullOrWhiteSpace(authToken))
                {
                    if (string.Equals(options.CallbackAuthHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation(
                            options.CallbackAuthHeaderName,
                            $"{options.CallbackAuthScheme} {authToken}".Trim());
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(options.CallbackAuthHeaderName, authToken);
                    }
                }

                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                try
                {
                    using var response = await client.SendAsync(request, timeoutCts.Token);
                    if (response.IsSuccessStatusCode)
                        return;

                    logger.LogWarning(
                        "OpenClaw bridge callback returned non-success status. status={StatusCode} event={EventType} attempt={Attempt}/{MaxAttempts}",
                        (int)response.StatusCode,
                        eventType,
                        attempt,
                        maxAttempts);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "OpenClaw bridge callback delivery failed. event={EventType} attempt={Attempt}/{MaxAttempts}",
                        eventType,
                        attempt,
                        maxAttempts);
                }

                if (attempt < maxAttempts)
                    await Task.Delay(retryDelayMs, CancellationToken.None);
            }
        }
        finally
        {
            adHocClient?.Dispose();
        }
    }

    private static bool IsAuthorized(HttpContext http, OpenClawBridgeOptions options, out string error)
    {
        error = "";
        var configuredToken = NormalizeToken(options.AuthToken);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            if (!options.RequireAuthToken)
                return true;

            error = "Bridge auth token is required but not configured.";
            return false;
        }

        var headerName = string.IsNullOrWhiteSpace(options.AuthHeaderName)
            ? "X-OpenClaw-Bridge-Token"
            : options.AuthHeaderName.Trim();
        var providedToken = NormalizeToken(http.Request.Headers[headerName].ToString());
        if (string.IsNullOrWhiteSpace(providedToken))
        {
            error = $"Missing auth header '{headerName}'.";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedToken),
                Encoding.UTF8.GetBytes(configuredToken)))
        {
            error = "Invalid bridge auth token.";
            return false;
        }

        return true;
    }

    private static string ResolvePrompt(OpenClawAgentHookInput input) =>
        FirstNonEmpty(input.Prompt, input.Message, input.Text);

    private static string ResolveSessionKey(
        string explicitSessionId,
        string channelId,
        string userId,
        string messageId)
    {
        var explicitSession = NormalizeToken(explicitSessionId);
        if (!string.IsNullOrWhiteSpace(explicitSession))
            return explicitSession;

        var channel = NormalizeToken(channelId);
        var user = NormalizeToken(userId);
        if (!string.IsNullOrWhiteSpace(channel) || !string.IsNullOrWhiteSpace(user))
            return $"{channel}:{user}".Trim(':');

        var message = NormalizeToken(messageId);
        return string.IsNullOrWhiteSpace(message) ? Guid.NewGuid().ToString("N") : message;
    }

    private static string ResolveIdempotencyKey(
        string explicitIdempotencyKey,
        string channelId,
        string userId,
        string messageId,
        string sessionKey)
    {
        var explicitKey = NormalizeToken(explicitIdempotencyKey);
        if (!string.IsNullOrWhiteSpace(explicitKey))
            return explicitKey;

        var channel = NormalizeToken(channelId);
        var user = NormalizeToken(userId);
        var message = NormalizeToken(messageId);
        var composed = $"{channel}:{user}:{message}".Trim(':');
        return string.IsNullOrWhiteSpace(composed) ? sessionKey : composed;
    }

    private static string ResolveCorrelationId(string messageId, string idempotencyKey)
    {
        var explicitMessageId = NormalizeToken(messageId);
        if (!string.IsNullOrWhiteSpace(explicitMessageId))
            return explicitMessageId;

        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : idempotencyKey;
    }

    private static string ResolveActorId(string explicitActorId, string sessionKey)
    {
        var normalizedActorId = NormalizeToken(explicitActorId);
        if (!string.IsNullOrWhiteSpace(normalizedActorId))
            return normalizedActorId;

        // Stable deterministic actor mapping: do not keep process-level session dictionaries.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"oc-{hash[..28]}";
    }

    private static bool TryNormalizeBridgeInput(
        OpenClawAgentHookInput input,
        out OpenClawNormalizedInput normalized,
        out string error)
    {
        normalized = new OpenClawNormalizedInput("", "", "", "", "", "", "", "");
        error = string.Empty;

        if (!TryNormalizeContextToken(input.ActorId, "actorId", 128, out var actorId, out error))
            return false;
        if (!TryNormalizeContextToken(input.SessionId, "sessionId", 256, out var sessionId, out error))
            return false;
        if (!TryNormalizeContextToken(input.ChannelId, "channelId", 128, out var channelId, out error))
            return false;
        if (!TryNormalizeContextToken(input.UserId, "userId", 128, out var userId, out error))
            return false;
        if (!TryNormalizeContextToken(input.MessageId, "messageId", 128, out var messageId, out error))
            return false;
        if (!TryNormalizeContextToken(input.IdempotencyKey, "idempotencyKey", 256, out var idempotencyKey, out error))
            return false;
        if (!TryNormalizeContextToken(input.CallbackUrl, "callbackUrl", 1024, out var callbackUrl, out error, allowUri: true))
            return false;
        if (!TryNormalizeContextToken(input.CallbackToken, "callbackToken", 2048, out var callbackToken, out error, allowLooseToken: true))
            return false;

        normalized = new OpenClawNormalizedInput(
            ActorId: actorId,
            SessionId: sessionId,
            ChannelId: channelId,
            UserId: userId,
            MessageId: messageId,
            IdempotencyKey: idempotencyKey,
            CallbackUrl: callbackUrl,
            CallbackToken: callbackToken);
        return true;
    }

    private static bool TryNormalizeContextToken(
        string? raw,
        string fieldName,
        int maxLength,
        out string normalized,
        out string error,
        bool allowUri = false,
        bool allowLooseToken = false)
    {
        normalized = NormalizeToken(raw);
        error = string.Empty;
        if (normalized.Length == 0)
            return true;
        if (normalized.Length > maxLength)
        {
            error = $"'{fieldName}' exceeds maximum length {maxLength}.";
            return false;
        }

        if (allowUri)
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                error = $"'{fieldName}' must be an absolute URI.";
                return false;
            }

            return true;
        }

        if (allowLooseToken)
            return true;

        if (!ContextTokenRegex.IsMatch(normalized))
        {
            error = $"'{fieldName}' contains invalid characters.";
            return false;
        }

        return true;
    }

    private static bool IsCallbackHostAllowed(Uri callbackUri, OpenClawBridgeOptions options)
    {
        if (options.CallbackAllowedHosts == null || options.CallbackAllowedHosts.Count == 0)
            return true;

        var host = NormalizeToken(callbackUri.Host);
        foreach (var rawAllowed in options.CallbackAllowedHosts)
        {
            var allowed = NormalizeToken(rawAllowed).ToLowerInvariant();
            if (allowed.Length == 0)
                continue;

            if (allowed.StartsWith(".", StringComparison.Ordinal))
            {
                if (host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildRunMetadata(OpenClawBridgeContext context)
    {
        var metadata = new Dictionary<string, string>(context.Metadata, StringComparer.Ordinal)
        {
            [WorkflowRunCommandMetadataKeys.SessionId] = context.SessionKey,
            [WorkflowRunCommandMetadataKeys.ChannelId] = context.ChannelId,
            [WorkflowRunCommandMetadataKeys.UserId] = context.UserId,
            [WorkflowRunCommandMetadataKeys.MessageId] = context.MessageId,
            [WorkflowRunCommandMetadataKeys.CorrelationId] = context.CorrelationId,
            [WorkflowRunCommandMetadataKeys.IdempotencyKey] = context.IdempotencyKey,
        };
        if (!string.IsNullOrWhiteSpace(context.CallbackUrl))
            metadata[WorkflowRunCommandMetadataKeys.CallbackUrl] = context.CallbackUrl;
        return metadata;
    }

    private static IResult BuildDuplicateRequestResult(
        OpenClawIdempotencyAcquireResult acquire,
        OpenClawBridgeContext context)
    {
        var record = acquire.Record;
        if (record == null)
        {
            return Results.Json(
                new
                {
                    code = "IDEMPOTENCY_CONFLICT",
                    message = "Request cannot be processed due to idempotency conflict.",
                    correlationId = context.CorrelationId,
                    idempotencyKey = context.IdempotencyKey,
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (acquire.Status == OpenClawIdempotencyAcquireStatus.ExistingPending)
        {
            return Results.Json(
                new
                {
                    code = "IDEMPOTENCY_IN_PROGRESS",
                    message = "A request with the same idempotencyKey is still in progress.",
                    correlationId = record.CorrelationId,
                    idempotencyKey = record.IdempotencyKey,
                    sessionKey = record.SessionKey,
                    channelId = record.ChannelId,
                    userId = record.UserId,
                    actorId = record.ActorId,
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (acquire.Status == OpenClawIdempotencyAcquireStatus.ExistingFailed)
        {
            return Results.Json(
                new
                {
                    code = "IDEMPOTENCY_PREVIOUSLY_FAILED",
                    message = "A previous request with the same idempotencyKey failed.",
                    correlationId = record.CorrelationId,
                    idempotencyKey = record.IdempotencyKey,
                    actorId = record.ActorId,
                    commandId = record.CommandId,
                    errorCode = record.LastErrorCode,
                    errorMessage = record.LastErrorMessage,
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Accepted(
            $"/api/actors/{record.ActorId}",
            new
            {
                accepted = true,
                replayed = true,
                actorId = record.ActorId,
                commandId = record.CommandId,
                workflow = record.WorkflowName,
                correlationId = record.CorrelationId,
                idempotencyKey = record.IdempotencyKey,
                sessionKey = record.SessionKey,
                channelId = record.ChannelId,
                userId = record.UserId,
            });
    }

    private static Task MarkIdempotencyStartedAsync(
        IOpenClawIdempotencyStore? idempotencyStore,
        string idempotencyKey,
        string actorId,
        string commandId,
        string workflowName,
        bool idempotencyAcquired,
        CancellationToken ct)
    {
        if (!idempotencyAcquired || idempotencyStore == null || string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.CompletedTask;
        return idempotencyStore.MarkStartedAsync(idempotencyKey, actorId, commandId, workflowName, ct);
    }

    private static Task MarkIdempotencyCompletedAsync(
        IOpenClawIdempotencyStore? idempotencyStore,
        string idempotencyKey,
        bool success,
        string errorCode,
        string errorMessage,
        bool idempotencyAcquired,
        CancellationToken ct)
    {
        if (!idempotencyAcquired || idempotencyStore == null || string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.CompletedTask;
        return idempotencyStore.MarkCompletedAsync(idempotencyKey, success, errorCode, errorMessage, ct);
    }

    private static Dictionary<string, string> CloneMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return [];

        var cloned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in metadata)
        {
            var key = NormalizeToken(rawKey);
            if (string.IsNullOrWhiteSpace(key))
                continue;
            cloned[key] = NormalizeToken(rawValue);
        }

        return cloned;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeToken(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }
        return string.Empty;
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string BuildEventId(string idempotencyKey, long sequence)
    {
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : idempotencyKey;
        return $"{key}:{sequence}";
    }

    private sealed record OpenClawNormalizedInput(
        string ActorId,
        string SessionId,
        string ChannelId,
        string UserId,
        string MessageId,
        string IdempotencyKey,
        string CallbackUrl,
        string CallbackToken);

    private sealed record OpenClawBridgeContext(
        string SessionKey,
        string IdempotencyKey,
        string CorrelationId,
        string ChannelId,
        string UserId,
        string MessageId,
        string CallbackUrl,
        string CallbackToken,
        string WorkflowName,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed class BridgeReceiptState
    {
        private readonly object _sync = new();
        private long _sequence;
        private string _actorId = string.Empty;
        private string _commandId = string.Empty;

        public string ActorId
        {
            get
            {
                lock (_sync)
                    return _actorId;
            }
        }

        public string CommandId
        {
            get
            {
                lock (_sync)
                    return _commandId;
            }
        }

        public long NextSequence() => Interlocked.Increment(ref _sequence);

        public void SetStarted(string? actorId, string? commandId)
        {
            lock (_sync)
            {
                var normalizedActorId = NormalizeToken(actorId);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                    _actorId = normalizedActorId;

                var normalizedCommandId = NormalizeToken(commandId);
                if (!string.IsNullOrWhiteSpace(normalizedCommandId))
                    _commandId = normalizedCommandId;
            }
        }
    }
}
