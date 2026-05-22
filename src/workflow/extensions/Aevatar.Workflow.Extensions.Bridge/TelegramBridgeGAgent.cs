using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Telegram channel bridge agent.
/// Handles ChatRequestEvent -> Telegram sendMessage and waitReply dispatch.
/// </summary>
[GAgent("workflow.telegram-bridge")]
// Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
//   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
//   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
// Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
//   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
//   New principle: New task-scoped TelegramWaitReplyGAgent owns wait state; bridge sends WaitForReplyCommand and resumes via WaitReplyCompleted/Failed event(reference lark stream actor architecture for unification)
public class TelegramBridgeGAgent : GAgentBase
{
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string WaitReplyOperation = "/waitReply";
    private const int DefaultWaitReplyTimeoutMs = 120_000;
    private const int DefaultPollTimeoutSeconds = 8;
    private const int DefaultSettlePollsAfterMatch = 1;
    private const int MaxSettlePollsAfterMatch = 5;
    private const int MaxPollTimeoutSeconds = 25;
    private readonly IActorRuntime _runtime;
    private readonly IConnectorRegistry _connectorRegistry;

    protected virtual string DefaultConnectorName => "telegram";

    public TelegramBridgeGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectorName = ReadMetadata(request.Headers, "telegram.connector", "connector", "connector_name");
        if (string.IsNullOrWhiteSpace(connectorName))
            connectorName = DefaultConnectorName;

        var chatId = ReadMetadata(request.Headers, "telegram.chat_id", "chat_id");
        if (string.IsNullOrWhiteSpace(chatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required");
            return;
        }

        var operation = ReadMetadata(request.Headers, "telegram.operation", "operation", "path");
        if (string.IsNullOrWhiteSpace(operation))
            operation = "/sendMessage";

        if (string.Equals(operation, WaitReplyOperation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "wait_reply", StringComparison.OrdinalIgnoreCase))
        {
            await HandleWaitReplyAsync(request, connectorName);
            return;
        }

        if (!_connectorRegistry.TryGet(connectorName, out var connector) || connector == null)
        {
            await PublishFailureAsync(request, $"telegram connector '{connectorName}' not found");
            return;
        }

        var requestPayload = BuildTelegramPayload(request, chatId.Trim());
        var connectorParameters = BuildConnectorParameters(request.Headers);
        var connectorRequest = new ConnectorRequest
        {
            RunId = ReadMetadata(request.Headers, "run_id", "workflow.run_id", "workflow_run_id", "session_id"),
            StepId = ReadMetadata(request.Headers, "step_id", "workflow.step_id", "workflow_step_id"),
            Connector = connectorName,
            Operation = operation,
            Payload = requestPayload,
            Parameters = connectorParameters,
        };

        var response = await ExecuteConnectorWithWatchdogAsync(
            connector,
            connectorRequest,
            ResolveConnectorExecutionWatchdogMs(connectorParameters));

        if (!response.Success)
        {
            var error = string.IsNullOrWhiteSpace(response.Error)
                ? "telegram connector call failed"
                : response.Error.Trim();
            await PublishFailureAsync(request, error);
            return;
        }

        var content = ExtractResponseContent(response.Output);
        await PublishSuccessAsync(request, content);
    }

    private async Task HandleWaitReplyAsync(
        ChatRequestEvent request,
        string connectorName)
    {
        var expectedChatId = ReadMetadata(request.Headers, "telegram.chat_id", "chat_id").Trim();
        if (string.IsNullOrWhiteSpace(expectedChatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required for /waitReply");
            return;
        }

        var expectedFromUserId = ReadMetadata(
            request.Headers,
            "telegram.expected_from_user_id",
            "expected_from_user_id",
            "from_user_id").Trim();
        var expectedFromUsername = NormalizeUsername(ReadMetadata(
            request.Headers,
            "telegram.expected_from_username",
            "expected_from_username",
            "from_username",
            "from_user"));
        var correlationContains = ReadMetadata(
            request.Headers,
            "telegram.correlation_contains",
            "correlation_contains",
            "contains").Trim();

        var waitTimeoutMs = ResolveWaitReplyTimeoutMs(request.Headers);
        var pollTimeoutSeconds = ResolvePollTimeoutSeconds(request.Headers);
        var settlePollsAfterMatch = ResolveSettlePollsAfterMatch(request.Headers);
        var collectAllReplies = ResolveCollectAllReplies(request.Headers);
        var startFromLatest = ResolveStartFromLatest(request.Headers);
        var connectorParameters = BuildConnectorParameters(request.Headers);
        var offset = TryReadInt64(
            ReadMetadata(request.Headers, "telegram.offset", "offset"),
            minimum: 0);

        var commandId = BuildWaitReplyCommandId(request);
        var waitActorId = BuildWaitReplyActorId(commandId);
        var waitActor = await _runtime.CreateAsync<TelegramWaitReplyGAgent>(waitActorId);
        await _runtime.LinkAsync(Id, waitActor.Id);
        var command = new TelegramWaitForReplyCommand
        {
            CommandId = commandId,
            SessionId = request.SessionId,
            ConnectorName = connectorName,
            ExpectedChatId = expectedChatId,
            ExpectedFromUserId = expectedFromUserId,
            ExpectedFromUsername = expectedFromUsername,
            CorrelationContains = correlationContains,
            WaitTimeoutMs = waitTimeoutMs,
            PollTimeoutSeconds = pollTimeoutSeconds,
            SettlePollsAfterMatch = settlePollsAfterMatch,
            CollectAllReplies = collectAllReplies,
            StartFromLatest = startFromLatest,
            EmitChatResponse = ShouldEmitChatResponse(request.Headers),
        };
        command.ConnectorParameters.Add(connectorParameters);
        if (offset.HasValue)
            command.Offset = offset.Value;

        await SendToAsync(waitActor.Id, command);
    }

    [EventHandler]
    public async Task HandleWaitReplyCompleted(TelegramWaitReplyCompletedEvent evt)
    {
        // Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
        //   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
        //   New principle: New task-scoped TelegramWaitReplyGAgent owns wait state; bridge sends WaitForReplyCommand and resumes via WaitReplyCompleted/Failed event(reference lark stream actor architecture for unification)
        ArgumentNullException.ThrowIfNull(evt);
        await PublishSuccessAsync(evt.SessionId, evt.Content, evt.EmitChatResponse);
    }

    [EventHandler]
    public async Task HandleWaitReplyFailed(TelegramWaitReplyFailedEvent evt)
    {
        // Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
        //   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
        //   New principle: New task-scoped TelegramWaitReplyGAgent owns wait state; bridge sends WaitForReplyCommand and resumes via WaitReplyCompleted/Failed event(reference lark stream actor architecture for unification)
        ArgumentNullException.ThrowIfNull(evt);
        await PublishFailureAsync(evt.SessionId, evt.Error);
    }

    private static string BuildWaitReplyCommandId(ChatRequestEvent request)
    {
        var runId = ReadMetadata(request.Headers, "run_id", "workflow.run_id", "workflow_run_id", "session_id");
        var stepId = ReadMetadata(request.Headers, "step_id", "workflow.step_id", "workflow_step_id");
        if (!string.IsNullOrWhiteSpace(runId) && !string.IsNullOrWhiteSpace(stepId))
            return $"telegram-wait-reply-{NormalizeActorIdSegment(runId)}-{NormalizeActorIdSegment(stepId)}";

        var seed = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId;
        return $"telegram-wait-reply-{NormalizeActorIdSegment(seed)}";
    }

    private static string BuildWaitReplyActorId(string commandId) =>
        NormalizeActorIdSegment(commandId);

    private static string NormalizeActorIdSegment(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
        Span<char> buffer = stackalloc char[Math.Min(trimmed.Length, 96)];
        var count = 0;
        foreach (var ch in trimmed)
        {
            if (count >= buffer.Length)
                break;
            buffer[count++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '-';
        }

        return new string(buffer[..count]).Trim('-');
    }

    private static int ResolveWaitReplyTimeoutMs(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var raw = ReadMetadata(
            metadata,
            "telegram.wait_timeout_ms",
            "wait_timeout_ms",
            "timeout_ms",
            "aevatar.llm_timeout_ms");
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return DefaultWaitReplyTimeoutMs;
    }

    private static int ResolvePollTimeoutSeconds(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var raw = ReadMetadata(metadata, "telegram.poll_timeout_sec", "poll_timeout_sec");
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return Math.Clamp(parsed, 1, MaxPollTimeoutSeconds);
        }

        return DefaultPollTimeoutSeconds;
    }

    private static int ResolveSettlePollsAfterMatch(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var raw = ReadMetadata(
            metadata,
            "telegram.settle_polls_after_match",
            "settle_polls_after_match");
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return Math.Clamp(parsed, 0, MaxSettlePollsAfterMatch);

        return DefaultSettlePollsAfterMatch;
    }

    private static bool ResolveCollectAllReplies(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var raw = ReadMetadata(
            metadata,
            "telegram.collect_all_replies",
            "collect_all_replies");
        return TryParseBool(raw, out var parsed) && parsed;
    }

    private static bool ResolveStartFromLatest(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var raw = ReadMetadata(metadata, "telegram.start_from_latest", "start_from_latest");
        return !TryParseBool(raw, out var parsed) || parsed;
    }

    private static long? TryReadInt64(string raw, long minimum)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!long.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return null;
        return parsed < minimum ? null : parsed;
    }

    private static string NormalizeUsername(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var normalized = raw.Trim();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    private static Dictionary<string, string> BuildConnectorParameters(
        Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = ReadMetadata(metadata, "telegram.http_method", "method", "http_method"),
            ["content_type"] = ReadMetadata(metadata, "telegram.content_type", "content_type"),
        };

        if (string.IsNullOrWhiteSpace(parameters["method"]))
            parameters["method"] = "POST";
        if (string.IsNullOrWhiteSpace(parameters["content_type"]))
            parameters["content_type"] = "application/json";

        var timeoutMs = ResolveConnectorTimeoutMs(metadata);
        if (timeoutMs.HasValue)
            parameters["timeout_ms"] = timeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Allow workflow-level human_input (or other dynamic variables) to pass
        // Telegram login values into telegram_user connector initialization.
        CopyMetadataValueToConnectorParameter(
            metadata,
            parameters,
            "phone_number",
            "telegram.phone_number",
            "telegram_user.phone_number",
            "phone_number");
        CopyMetadataValueToConnectorParameter(
            metadata,
            parameters,
            "verification_code",
            "telegram.verification_code",
            "telegram_user.verification_code",
            "verification_code");
        CopyMetadataValueToConnectorParameter(
            metadata,
            parameters,
            "password",
            "telegram.2fa_password",
            "telegram.password",
            "telegram_user.2fa_password",
            "telegram_user.password",
            "2fa_password",
            "password");

        return parameters;
    }

    private static void CopyMetadataValueToConnectorParameter(
        Google.Protobuf.Collections.MapField<string, string> metadata,
        IDictionary<string, string> connectorParameters,
        string connectorKey,
        params string[] metadataKeys)
    {
        var value = ReadMetadata(metadata, metadataKeys);
        if (string.IsNullOrWhiteSpace(value))
            return;

        connectorParameters[connectorKey] = value.Trim();
    }

    private static int? ResolveConnectorTimeoutMs(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var explicitConnectorTimeout = TryReadPositiveInt32(ReadMetadata(metadata, "telegram.timeout_ms"));
        if (explicitConnectorTimeout.HasValue)
            return explicitConnectorTimeout.Value;

        var llmTimeout = TryReadPositiveInt32(ReadMetadata(metadata, "aevatar.llm_timeout_ms"));
        var requestedTimeout = TryReadPositiveInt32(ReadMetadata(metadata, "timeout_ms"));
        var candidate = requestedTimeout ?? llmTimeout;
        if (!candidate.HasValue)
            return null;

        if (llmTimeout.HasValue && candidate.Value >= llmTimeout.Value)
        {
            // Keep connector timeout slightly below LLM watchdog to avoid "LLM timed out first" races.
            candidate = Math.Max(100, llmTimeout.Value - 1000);
        }

        return candidate.Value;
    }

    private static int? TryReadPositiveInt32(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return null;
        return parsed > 0 ? parsed : null;
    }

    internal static int ResolveConnectorExecutionWatchdogMs(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return Math.Clamp(parsed, 100, 300_000);
        }

        return 20_000;
    }

    internal static async Task<ConnectorResponse> ExecuteConnectorWithWatchdogAsync(
        IConnector connector,
        ConnectorRequest connectorRequest,
        int watchdogTimeoutMs)
    {
        var timeoutMs = Math.Clamp(watchdogTimeoutMs, 100, 300_000);
        using var timeoutCts = new CancellationTokenSource();

        Task<ConnectorResponse> executeTask;
        try
        {
            executeTask = connector.ExecuteAsync(connectorRequest, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector execution failed: {ex.Message}",
            };
        }

        var timeoutTask = Task.Delay(timeoutMs);
        var completedTask = await Task.WhenAny(executeTask, timeoutTask);
        if (completedTask != executeTask)
        {
            timeoutCts.Cancel();
            _ = executeTask.ContinueWith(
                static completed =>
                {
                    _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector watchdog timeout after {timeoutMs}ms",
            };
        }

        try
        {
            timeoutCts.Cancel();
            return await executeTask;
        }
        catch (OperationCanceledException)
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector execution canceled after {timeoutMs}ms",
            };
        }
        catch (Exception ex)
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector execution failed: {ex.Message}",
            };
        }
    }

    private static string BuildTelegramPayload(ChatRequestEvent request, string chatId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = request.Prompt ?? string.Empty,
        };

        var threadId = ReadMetadata(request.Headers, "telegram.message_thread_id", "message_thread_id");
        if (!string.IsNullOrWhiteSpace(threadId) && long.TryParse(threadId, out var parsedThreadId))
            payload["message_thread_id"] = parsedThreadId;

        var parseMode = ReadMetadata(request.Headers, "telegram.parse_mode", "parse_mode");
        if (!string.IsNullOrWhiteSpace(parseMode))
            payload["parse_mode"] = parseMode.Trim();

        var disablePreview = ReadMetadata(
            request.Headers,
            "telegram.disable_web_page_preview",
            "disable_web_page_preview");
        if (TryParseBool(disablePreview, out var parsedDisablePreview))
            payload["disable_web_page_preview"] = parsedDisablePreview;

        var replyToMessageId = ReadMetadata(request.Headers, "telegram.reply_to_message_id", "reply_to_message_id");
        if (!string.IsNullOrWhiteSpace(replyToMessageId) && long.TryParse(replyToMessageId, out var parsedReplyToMessageId))
            payload["reply_to_message_id"] = parsedReplyToMessageId;

        return JsonSerializer.Serialize(payload);
    }

    private static string ExtractResponseContent(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // ignore parse failures and return raw connector output
        }

        return output;
    }

    private async Task PublishSuccessAsync(ChatRequestEvent request, string content)
    {
        await PublishSuccessAsync(request.SessionId, content, ShouldEmitChatResponse(request.Headers));
    }

    private async Task PublishSuccessAsync(string sessionId, string content, bool emitChatResponse)
    {
        if (emitChatResponse)
        {
            await PublishAsync(
                new ChatResponseEvent
                {
                    SessionId = sessionId,
                    Content = content,
                },
                TopologyAudience.Parent);
        }

        await PublishAsync(
            new TextMessageEndEvent
            {
                SessionId = sessionId,
                Content = content,
            },
            TopologyAudience.Parent);
    }

    private async Task PublishFailureAsync(ChatRequestEvent request, string error)
    {
        await PublishFailureAsync(request.SessionId, error);
    }

    private async Task PublishFailureAsync(string sessionId, string error)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "telegram bridge call failed" : error.Trim();
        await PublishAsync(
            new TextMessageEndEvent
            {
                SessionId = sessionId,
                Content = $"{LlmFailureContentPrefix} {safeError}",
            },
            TopologyAudience.Parent);
    }

    private static bool ShouldEmitChatResponse(Google.Protobuf.Collections.MapField<string, string> metadata)
    {
        var value = ReadMetadata(metadata, "telegram.emit_chat_response", "emit_chat_response");
        return TryParseBool(value, out var parsed) && parsed;
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim();
        if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static string ReadMetadata(
        Google.Protobuf.Collections.MapField<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var exact))
                return exact ?? string.Empty;
        }

        foreach (var (existingKey, value) in metadata)
        {
            foreach (var key in keys)
            {
                if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                    return value ?? string.Empty;
            }
        }

        return string.Empty;
    }

}
