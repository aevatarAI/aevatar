using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aevatar.Workflow.Application.Abstractions.OpenClaw;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.OpenClaw;

public sealed class OpenClawBridgeOrchestrationService : IOpenClawBridgeOrchestrationService
{
    private const int HttpAccepted = 202;
    private const int HttpBadRequest = 400;
    private const int HttpConflict = 409;
    private const int HttpInternalServerError = 500;
    private static readonly Regex ContextTokenRegex =
        new("^[A-Za-z0-9._:@#/\\-]+$", RegexOptions.Compiled);

    private readonly IWorkflowRunCommandService _runCommandService;
    private readonly IOpenClawBridgeReceiptDispatcher? _receiptDispatcher;
    private readonly IOpenClawIdempotencyStore? _idempotencyStore;
    private readonly ILogger<OpenClawBridgeOrchestrationService> _logger;

    public OpenClawBridgeOrchestrationService(
        IWorkflowRunCommandService runCommandService,
        IOpenClawBridgeReceiptDispatcher? receiptDispatcher = null,
        IOpenClawIdempotencyStore? idempotencyStore = null,
        ILogger<OpenClawBridgeOrchestrationService>? logger = null)
    {
        _runCommandService = runCommandService;
        _receiptDispatcher = receiptDispatcher;
        _idempotencyStore = idempotencyStore;
        _logger = logger ?? NullLogger<OpenClawBridgeOrchestrationService>.Instance;
    }

    public async Task<OpenClawBridgeExecutionResult> ExecuteAsync(
        OpenClawBridgeExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = ResolvePrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpBadRequest,
                "INVALID_PROMPT",
                "prompt/message/text is required.");
        }

        if (!TryNormalizeBridgeInput(request, out var normalizedInput, out var normalizeError))
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpBadRequest,
                "INVALID_CONTEXT",
                normalizeError);
        }

        if (!TryValidateCallback(normalizedInput.CallbackUrl, request.CallbackAllowedHosts, out var callbackError))
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpBadRequest,
                "CALLBACK_HOST_NOT_ALLOWED",
                callbackError);
        }

        var workflow = ResolveWorkflow(request);
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
        var metadata = CloneMetadata(request.Metadata);
        metadata["session_id"] = sessionKey;
        metadata["channel_id"] = normalizedInput.ChannelId;
        metadata["user_id"] = normalizedInput.UserId;
        metadata["message_id"] = normalizedInput.MessageId;
        metadata["correlation_id"] = correlationId;
        metadata["idempotency_key"] = idempotencyKey;

        var context = new OpenClawBridgeExecutionContext(
            SessionKey: sessionKey,
            IdempotencyKey: idempotencyKey,
            CorrelationId: correlationId,
            ChannelId: normalizedInput.ChannelId,
            UserId: normalizedInput.UserId,
            MessageId: normalizedInput.MessageId,
            WorkflowName: workflow,
            Metadata: metadata,
            Dispatch: new OpenClawBridgeDispatchPolicy(
                CallbackUrl: normalizedInput.CallbackUrl,
                CallbackToken: normalizedInput.CallbackToken,
                AuthHeaderName: string.IsNullOrWhiteSpace(request.CallbackAuthHeaderName) ? "Authorization" : request.CallbackAuthHeaderName.Trim(),
                AuthScheme: string.IsNullOrWhiteSpace(request.CallbackAuthScheme) ? "Bearer" : request.CallbackAuthScheme.Trim(),
                TimeoutMs: request.CallbackTimeoutMs,
                MaxAttempts: request.CallbackMaxAttempts,
                RetryDelayMs: request.CallbackRetryDelayMs));

        var idempotencyAcquired = false;
        if (request.EnableIdempotency &&
            _idempotencyStore != null &&
            !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var acquire = await _idempotencyStore.AcquireAsync(
                new OpenClawIdempotencyAcquireRequest(
                    IdempotencyKey: idempotencyKey,
                    SessionKey: sessionKey,
                    CorrelationId: correlationId,
                    ActorId: actorId,
                    WorkflowName: workflow,
                    ChannelId: context.ChannelId,
                    UserId: context.UserId,
                    MessageId: context.MessageId,
                    TtlHours: request.IdempotencyTtlHours),
                ct);

            if (acquire.Status != OpenClawIdempotencyAcquireStatus.Acquired)
                return BuildDuplicateRequestResult(acquire, context);

            idempotencyAcquired = true;
        }

        var runRequest = BuildRunRequest(request, prompt, workflow, actorId, context);
        var receiptState = new BridgeReceiptState();
        var startSignal = new TaskCompletionSource<WorkflowChatRunStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkflowChatRunExecutionResult> executionTask;
        try
        {
            executionTask = _runCommandService.ExecuteAsync(
                runRequest,
                (frame, token) => TryDispatchReceiptAsync(
                    context,
                    receiptState,
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
                    actorId: string.IsNullOrWhiteSpace(receiptState.ActorId) ? frame.ThreadId : receiptState.ActorId,
                    commandId: receiptState.CommandId,
                    token),
                onStartedAsync: async (started, token) =>
                {
                    receiptState.SetStarted(started.ActorId, started.CommandId);
                    startSignal.TrySetResult(started);
                    await MarkIdempotencyStartedAsync(
                        idempotencyKey,
                        started.ActorId,
                        started.CommandId,
                        started.WorkflowName,
                        idempotencyAcquired,
                        token);
                    await TryDispatchReceiptAsync(
                        context,
                        receiptState,
                        "aevatar.workflow.started",
                        new
                        {
                            started.ActorId,
                            started.CommandId,
                            started.WorkflowName,
                        },
                        actorId: started.ActorId,
                        commandId: started.CommandId,
                        token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenClaw bridge failed to start workflow execution.");
            await TryDispatchReceiptAsync(
                context,
                receiptState,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Bridge failed before workflow start.",
                    error = ex.Message,
                },
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Bridge failed before workflow start.",
                idempotencyAcquired,
                CancellationToken.None);
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpInternalServerError,
                "EXECUTION_FAILED",
                "Bridge failed before workflow start.",
                correlationId,
                idempotencyKey);
        }

        var completed = await Task.WhenAny(startSignal.Task, executionTask);
        if (completed == startSignal.Task)
        {
            var started = await startSignal.Task;
            _ = ObserveExecutionCompletionAsync(
                executionTask,
                context,
                receiptState,
                idempotencyKey,
                idempotencyAcquired);

            return OpenClawBridgeExecutionResult.AcceptedResult(
                started.ActorId,
                started.CommandId,
                started.WorkflowName,
                correlationId,
                idempotencyKey,
                sessionKey,
                context.ChannelId,
                context.UserId);
        }

        WorkflowChatRunExecutionResult result;
        try
        {
            result = await executionTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenClaw bridge execution task failed before start signal.");
            await TryDispatchReceiptAsync(
                context,
                receiptState,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Workflow execution failed.",
                    error = ex.Message,
                },
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Workflow execution failed.",
                idempotencyAcquired,
                CancellationToken.None);
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpInternalServerError,
                "EXECUTION_FAILED",
                "Workflow execution failed.",
                correlationId,
                idempotencyKey);
        }

        if (result.Error != WorkflowChatRunStartError.None)
        {
            var mappedError = MapRunStartError(result.Error);
            await TryDispatchReceiptAsync(
                context,
                receiptState,
                "aevatar.workflow.rejected",
                new
                {
                    mappedError.Code,
                    mappedError.Message,
                },
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyKey,
                success: false,
                errorCode: mappedError.Code,
                errorMessage: mappedError.Message,
                idempotencyAcquired,
                CancellationToken.None);
            return OpenClawBridgeExecutionResult.ErrorResult(
                mappedError.StatusCode,
                mappedError.Code,
                mappedError.Message,
                correlationId,
                idempotencyKey);
        }

        if (result.Started != null)
        {
            receiptState.SetStarted(result.Started.ActorId, result.Started.CommandId);
            await MarkIdempotencyStartedAsync(
                idempotencyKey,
                result.Started.ActorId,
                result.Started.CommandId,
                result.Started.WorkflowName,
                idempotencyAcquired,
                CancellationToken.None);
            if (result.FinalizeResult != null)
            {
                await TryDispatchReceiptAsync(
                    context,
                    receiptState,
                    "aevatar.workflow.completed",
                    new
                    {
                        projectionStatus = result.FinalizeResult.ProjectionCompletionStatus.ToString(),
                        projectionCompleted = result.FinalizeResult.ProjectionCompleted,
                        actorId = result.Started.ActorId,
                        commandId = result.Started.CommandId,
                    },
                    actorId: result.Started.ActorId,
                    commandId: result.Started.CommandId,
                    CancellationToken.None);
                await MarkIdempotencyCompletedAsync(
                    idempotencyKey,
                    success: true,
                    errorCode: string.Empty,
                    errorMessage: string.Empty,
                    idempotencyAcquired,
                    CancellationToken.None);
            }

            return OpenClawBridgeExecutionResult.AcceptedResult(
                result.Started.ActorId,
                result.Started.CommandId,
                result.Started.WorkflowName,
                correlationId,
                idempotencyKey,
                sessionKey,
                context.ChannelId,
                context.UserId);
        }

        await MarkIdempotencyCompletedAsync(
            idempotencyKey,
            success: false,
            errorCode: "EXECUTION_FAILED",
            errorMessage: "Workflow execution did not produce a start signal.",
            idempotencyAcquired,
            CancellationToken.None);
        return OpenClawBridgeExecutionResult.ErrorResult(
            HttpInternalServerError,
            "EXECUTION_FAILED",
            "Workflow execution did not produce a start signal.",
            correlationId,
            idempotencyKey);
    }

    private async Task ObserveExecutionCompletionAsync(
        Task<WorkflowChatRunExecutionResult> executionTask,
        OpenClawBridgeExecutionContext context,
        BridgeReceiptState receiptState,
        string idempotencyKey,
        bool idempotencyAcquired)
    {
        try
        {
            var result = await executionTask.ConfigureAwait(false);
            if (result.Error == WorkflowChatRunStartError.None && result.FinalizeResult != null)
            {
                receiptState.SetStarted(result.Started?.ActorId, result.Started?.CommandId);
                await TryDispatchReceiptAsync(
                    context,
                    receiptState,
                    "aevatar.workflow.completed",
                    new
                    {
                        projectionStatus = result.FinalizeResult.ProjectionCompletionStatus.ToString(),
                        projectionCompleted = result.FinalizeResult.ProjectionCompleted,
                        actorId = result.Started?.ActorId ?? string.Empty,
                        commandId = result.Started?.CommandId ?? string.Empty,
                    },
                    actorId: result.Started?.ActorId,
                    commandId: result.Started?.CommandId,
                    CancellationToken.None);
                await MarkIdempotencyCompletedAsync(
                    idempotencyKey,
                    success: true,
                    errorCode: string.Empty,
                    errorMessage: string.Empty,
                    idempotencyAcquired,
                    CancellationToken.None);
                return;
            }

            var mappedError = MapRunStartError(result.Error);
            await TryDispatchReceiptAsync(
                context,
                receiptState,
                "aevatar.workflow.failed",
                new
                {
                    mappedError.Code,
                    mappedError.Message,
                    actorId = result.Started?.ActorId ?? string.Empty,
                    commandId = result.Started?.CommandId ?? string.Empty,
                },
                actorId: result.Started?.ActorId,
                commandId: result.Started?.CommandId,
                CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyKey,
                success: false,
                errorCode: mappedError.Code,
                errorMessage: mappedError.Message,
                idempotencyAcquired,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenClaw bridge failed while observing workflow completion.");
            await TryDispatchReceiptAsync(
                context,
                receiptState,
                "aevatar.workflow.failed",
                new
                {
                    code = "EXECUTION_FAILED",
                    message = "Workflow execution failed after start.",
                    error = ex.Message,
                },
                ct: CancellationToken.None);
            await MarkIdempotencyCompletedAsync(
                idempotencyKey,
                success: false,
                errorCode: "EXECUTION_FAILED",
                errorMessage: "Workflow execution failed after start.",
                idempotencyAcquired,
                CancellationToken.None);
        }
    }

    private async ValueTask TryDispatchReceiptAsync(
        OpenClawBridgeExecutionContext context,
        BridgeReceiptState receiptState,
        string eventType,
        object payload,
        string? actorId = null,
        string? commandId = null,
        CancellationToken ct = default)
    {
        if (_receiptDispatcher == null || string.IsNullOrWhiteSpace(context.Dispatch.CallbackUrl))
            return;

        var sequence = receiptState.NextSequence();
        var resolvedActorId = NormalizeToken(actorId);
        if (string.IsNullOrWhiteSpace(resolvedActorId))
            resolvedActorId = receiptState.ActorId;
        var resolvedCommandId = NormalizeToken(commandId);
        if (string.IsNullOrWhiteSpace(resolvedCommandId))
            resolvedCommandId = receiptState.CommandId;

        await _receiptDispatcher.DispatchAsync(
            new OpenClawBridgeReceiptDispatchRequest
            {
                CallbackUrl = context.Dispatch.CallbackUrl,
                CallbackToken = context.Dispatch.CallbackToken,
                EventId = BuildEventId(context.IdempotencyKey, sequence),
                Sequence = sequence,
                EventType = eventType,
                CorrelationId = context.CorrelationId,
                IdempotencyKey = context.IdempotencyKey,
                SessionKey = context.SessionKey,
                ChannelId = context.ChannelId,
                UserId = context.UserId,
                MessageId = context.MessageId,
                ActorId = resolvedActorId,
                CommandId = resolvedCommandId,
                WorkflowName = context.WorkflowName,
                Metadata = context.Metadata,
                Payload = payload,
                AuthHeaderName = context.Dispatch.AuthHeaderName,
                AuthScheme = context.Dispatch.AuthScheme,
                TimeoutMs = context.Dispatch.TimeoutMs,
                MaxAttempts = context.Dispatch.MaxAttempts,
                RetryDelayMs = context.Dispatch.RetryDelayMs,
            },
            ct);
    }

    private Task MarkIdempotencyStartedAsync(
        string idempotencyKey,
        string actorId,
        string commandId,
        string workflowName,
        bool idempotencyAcquired,
        CancellationToken ct)
    {
        if (!idempotencyAcquired || _idempotencyStore == null || string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.CompletedTask;
        return _idempotencyStore.MarkStartedAsync(idempotencyKey, actorId, commandId, workflowName, ct);
    }

    private Task MarkIdempotencyCompletedAsync(
        string idempotencyKey,
        bool success,
        string errorCode,
        string errorMessage,
        bool idempotencyAcquired,
        CancellationToken ct)
    {
        if (!idempotencyAcquired || _idempotencyStore == null || string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.CompletedTask;
        return _idempotencyStore.MarkCompletedAsync(idempotencyKey, success, errorCode, errorMessage, ct);
    }

    private static WorkflowChatRunRequest BuildRunRequest(
        OpenClawBridgeExecutionRequest request,
        string prompt,
        string workflow,
        string actorId,
        OpenClawBridgeExecutionContext context)
    {
        var metadata = BuildRunMetadata(context);
        var normalizedYamls = NormalizeInlineWorkflowYamls(request.WorkflowYamls);
        if (normalizedYamls.Count > 0)
        {
            return new WorkflowChatRunRequest(
                prompt,
                WorkflowName: null,
                ActorId: actorId,
                WorkflowYamls: normalizedYamls,
                Metadata: metadata);
        }

        return new WorkflowChatRunRequest(
            prompt,
            workflow,
            actorId,
            WorkflowYamls: null,
            Metadata: metadata);
    }

    private static IReadOnlyList<string> NormalizeInlineWorkflowYamls(IReadOnlyList<string>? workflowYamls)
    {
        if (workflowYamls == null || workflowYamls.Count == 0)
            return [];

        var normalized = new List<string>(workflowYamls.Count);
        foreach (var yaml in workflowYamls)
            normalized.Add(yaml ?? string.Empty);
        return normalized;
    }

    private static Dictionary<string, string> BuildRunMetadata(OpenClawBridgeExecutionContext context)
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
        if (!string.IsNullOrWhiteSpace(context.Dispatch.CallbackUrl))
            metadata[WorkflowRunCommandMetadataKeys.CallbackUrl] = context.Dispatch.CallbackUrl;
        return metadata;
    }

    private static OpenClawBridgeExecutionResult BuildDuplicateRequestResult(
        OpenClawIdempotencyAcquireResult acquire,
        OpenClawBridgeExecutionContext context)
    {
        var record = acquire.Record;
        if (record == null)
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpConflict,
                "IDEMPOTENCY_CONFLICT",
                "Request cannot be processed due to idempotency conflict.",
                context.CorrelationId,
                context.IdempotencyKey,
                context.SessionKey,
                context.ChannelId,
                context.UserId);
        }

        if (acquire.Status == OpenClawIdempotencyAcquireStatus.ExistingPending)
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpConflict,
                "IDEMPOTENCY_IN_PROGRESS",
                "A request with the same idempotencyKey is still in progress.",
                record.CorrelationId,
                record.IdempotencyKey,
                record.SessionKey,
                record.ChannelId,
                record.UserId,
                record.ActorId,
                record.CommandId,
                record.WorkflowName);
        }

        if (acquire.Status == OpenClawIdempotencyAcquireStatus.ExistingFailed)
        {
            return OpenClawBridgeExecutionResult.ErrorResult(
                HttpConflict,
                "IDEMPOTENCY_PREVIOUSLY_FAILED",
                "A previous request with the same idempotencyKey failed.",
                record.CorrelationId,
                record.IdempotencyKey,
                record.SessionKey,
                record.ChannelId,
                record.UserId,
                record.ActorId,
                record.CommandId,
                record.WorkflowName);
        }

        return OpenClawBridgeExecutionResult.AcceptedResult(
            record.ActorId,
            record.CommandId,
            record.WorkflowName,
            record.CorrelationId,
            record.IdempotencyKey,
            record.SessionKey,
            record.ChannelId,
            record.UserId,
            replayed: true);
    }

    private static (int StatusCode, string Code, string Message) MapRunStartError(WorkflowChatRunStartError error) =>
        error switch
        {
            WorkflowChatRunStartError.AgentNotFound => (404, "AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => (404, "WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => (400, "AGENT_TYPE_NOT_SUPPORTED", "Agent is not WorkflowGAgent."),
            WorkflowChatRunStartError.ProjectionDisabled => (503, "PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => (409, "WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => (409, "AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => (400, "INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => (400, "WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            _ => (400, "RUN_START_FAILED", "Failed to resolve actor."),
        };

    private static string ResolvePrompt(OpenClawBridgeExecutionRequest request) =>
        FirstNonEmpty(request.Prompt, request.Message, request.Text);

    private static string ResolveWorkflow(OpenClawBridgeExecutionRequest request)
    {
        if (request.WorkflowYamls is { Count: > 0 })
            return string.Empty;

        var explicitWorkflow = NormalizeToken(request.Workflow);
        if (!string.IsNullOrWhiteSpace(explicitWorkflow))
            return explicitWorkflow;

        var defaultWorkflow = NormalizeToken(request.DefaultWorkflowName);
        if (!string.IsNullOrWhiteSpace(defaultWorkflow))
            return defaultWorkflow;

        return WorkflowRunBehaviorOptions.AutoWorkflowName;
    }

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

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"oc-{hash[..28]}";
    }

    private static bool TryNormalizeBridgeInput(
        OpenClawBridgeExecutionRequest request,
        out OpenClawNormalizedInput normalized,
        out string error)
    {
        normalized = new OpenClawNormalizedInput("", "", "", "", "", "", "", "", "");
        error = string.Empty;

        if (!TryNormalizeContextToken(request.ActorId, "actorId", 128, out var actorId, out error))
            return false;
        if (!TryNormalizeContextToken(request.Workflow, "workflow", 256, out var workflow, out error))
            return false;
        if (!TryNormalizeContextToken(request.SessionId, "sessionId", 256, out var sessionId, out error))
            return false;
        if (!TryNormalizeContextToken(request.ChannelId, "channelId", 128, out var channelId, out error))
            return false;
        if (!TryNormalizeContextToken(request.UserId, "userId", 128, out var userId, out error))
            return false;
        if (!TryNormalizeContextToken(request.MessageId, "messageId", 128, out var messageId, out error))
            return false;
        if (!TryNormalizeContextToken(request.IdempotencyKey, "idempotencyKey", 256, out var idempotencyKey, out error))
            return false;
        if (!TryNormalizeContextToken(request.CallbackUrl, "callbackUrl", 1024, out var callbackUrl, out error, allowUri: true))
            return false;
        if (!TryNormalizeContextToken(request.CallbackToken, "callbackToken", 2048, out var callbackToken, out error, allowLooseToken: true))
            return false;

        normalized = new OpenClawNormalizedInput(
            ActorId: actorId,
            Workflow: workflow,
            SessionId: sessionId,
            ChannelId: channelId,
            UserId: userId,
            MessageId: messageId,
            IdempotencyKey: idempotencyKey,
            CallbackUrl: callbackUrl,
            CallbackToken: callbackToken);
        return true;
    }

    private static bool TryValidateCallback(
        string callbackUrl,
        IReadOnlyList<string>? allowedHosts,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            return true;

        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callbackUri))
        {
            error = "'callbackUrl' must be an absolute URI.";
            return false;
        }

        if (allowedHosts == null || allowedHosts.Count == 0)
        {
            error = "'callbackUrl' is not allowed because no callback hosts are configured.";
            return false;
        }

        var host = NormalizeToken(callbackUri.Host);
        foreach (var rawAllowed in allowedHosts)
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

        error = $"'callbackUrl' host '{callbackUri.Host}' is not allowed.";
        return false;
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

    private static Dictionary<string, string> CloneMetadata(IReadOnlyDictionary<string, string>? metadata)
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

    private static string BuildEventId(string idempotencyKey, long sequence)
    {
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : idempotencyKey;
        return $"{key}:{sequence}";
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private sealed record OpenClawNormalizedInput(
        string ActorId,
        string Workflow,
        string SessionId,
        string ChannelId,
        string UserId,
        string MessageId,
        string IdempotencyKey,
        string CallbackUrl,
        string CallbackToken);

    private sealed record OpenClawBridgeExecutionContext(
        string SessionKey,
        string IdempotencyKey,
        string CorrelationId,
        string ChannelId,
        string UserId,
        string MessageId,
        string WorkflowName,
        IReadOnlyDictionary<string, string> Metadata,
        OpenClawBridgeDispatchPolicy Dispatch);

    private sealed record OpenClawBridgeDispatchPolicy(
        string CallbackUrl,
        string CallbackToken,
        string AuthHeaderName,
        string AuthScheme,
        int TimeoutMs,
        int MaxAttempts,
        int RetryDelayMs);

    private sealed class BridgeReceiptState
    {
        private long _sequence;
        private string _actorId = string.Empty;
        private string _commandId = string.Empty;

        public string ActorId => _actorId;

        public string CommandId => _commandId;

        public long NextSequence() => Interlocked.Increment(ref _sequence);

        public void SetStarted(string? actorId, string? commandId)
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
