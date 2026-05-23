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
// Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
//   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
//   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
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

        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        var telegram = request.Telegram;
        var connectorName = Normalize(telegram?.ConnectorName);
        if (string.IsNullOrWhiteSpace(connectorName))
            connectorName = DefaultConnectorName;

        var chatId = Normalize(telegram?.ChatId);
        if (string.IsNullOrWhiteSpace(chatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required");
            return;
        }

        var operation = ResolveOperation(telegram);
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
        var connectorParameters = BuildConnectorParameters(request);
        var connectorRequest = new ConnectorRequest
        {
            RunId = Normalize(telegram?.RunId),
            StepId = Normalize(telegram?.StepId),
            Connector = connectorName,
            Operation = operation,
            Payload = requestPayload,
            Parameters = connectorParameters,
        };

        ConnectorResponse response;
        try
        {
            response = await connector.ExecuteAsync(connectorRequest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            response = new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector execution failed: {ex.Message}",
            };
        }

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
        var telegram = request.Telegram;
        var expectedChatId = Normalize(telegram?.ChatId);
        if (string.IsNullOrWhiteSpace(expectedChatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required for /waitReply");
            return;
        }

        var expectedFromUserId = Normalize(telegram?.ExpectedFromUserId);
        var expectedFromUsername = NormalizeUsername(telegram?.ExpectedFromUsername);
        var correlationContains = Normalize(telegram?.CorrelationContains);

        var waitTimeoutMs = ResolveWaitReplyTimeoutMs(telegram);
        var pollTimeoutSeconds = ResolvePollTimeoutSeconds(telegram);
        var settlePollsAfterMatch = ResolveSettlePollsAfterMatch(telegram);
        var collectAllReplies = telegram?.CollectAllReplies == true;
        var startFromLatest = telegram?.HasStartFromLatest != true || telegram.StartFromLatest;
        var connectorParameters = BuildConnectorParameters(request);
        var offset = telegram?.Offset > 0 ? telegram.Offset : (long?)null;

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
            EmitChatResponse = telegram?.EmitChatResponse == true,
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
        var runId = Normalize(request.Telegram?.RunId);
        var stepId = Normalize(request.Telegram?.StepId);
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

    private static int ResolveWaitReplyTimeoutMs(TelegramBridgeRequest? telegram)
    {
        return telegram?.HasWaitTimeoutMs == true && telegram.WaitTimeoutMs > 0
            ? telegram.WaitTimeoutMs
            : DefaultWaitReplyTimeoutMs;
    }

    private static int ResolvePollTimeoutSeconds(TelegramBridgeRequest? telegram)
    {
        return telegram?.HasPollTimeoutSeconds == true
            ? Math.Clamp(telegram.PollTimeoutSeconds, 0, MaxPollTimeoutSeconds)
            : DefaultPollTimeoutSeconds;
    }

    private static int ResolveSettlePollsAfterMatch(TelegramBridgeRequest? telegram)
    {
        return telegram?.HasSettlePollsAfterMatch == true
            ? Math.Clamp(telegram.SettlePollsAfterMatch, 0, MaxSettlePollsAfterMatch)
            : DefaultSettlePollsAfterMatch;
    }

    private static string NormalizeUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var normalized = raw.Trim();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    private static Dictionary<string, string> BuildConnectorParameters(
        ChatRequestEvent request)
    {
        var telegram = request.Telegram;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = Normalize(telegram?.HttpMethod),
            ["content_type"] = Normalize(telegram?.ContentType),
        };

        if (string.IsNullOrWhiteSpace(parameters["method"]))
            parameters["method"] = "POST";
        if (string.IsNullOrWhiteSpace(parameters["content_type"]))
            parameters["content_type"] = "application/json";

        var timeoutMs = ResolveConnectorTimeoutMs(request);
        if (timeoutMs.HasValue)
            parameters["timeout_ms"] = timeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        AddParameterIfNotBlank(parameters, "phone_number", telegram?.PhoneNumber);
        AddParameterIfNotBlank(parameters, "verification_code", telegram?.VerificationCode);
        AddParameterIfNotBlank(parameters, "password", telegram?.Password);

        return parameters;
    }

    private static void AddParameterIfNotBlank(
        IDictionary<string, string> connectorParameters,
        string connectorKey,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        connectorParameters[connectorKey] = value.Trim();
    }

    private static int? ResolveConnectorTimeoutMs(ChatRequestEvent request)
    {
        var explicitConnectorTimeout =
            request.Telegram?.HasTimeoutMs == true && request.Telegram.TimeoutMs > 0
                ? request.Telegram.TimeoutMs
                : (int?)null;
        var llmTimeout = request.TimeoutMs > 0 ? request.TimeoutMs : (int?)null;
        var candidate = explicitConnectorTimeout ?? llmTimeout;
        if (!candidate.HasValue)
            return null;

        if (!explicitConnectorTimeout.HasValue && llmTimeout.HasValue && candidate.Value >= llmTimeout.Value)
        {
            // Keep connector timeout slightly below LLM watchdog to avoid "LLM timed out first" races.
            candidate = Math.Max(100, llmTimeout.Value - 1000);
        }

        return candidate.Value;
    }

    private static string BuildTelegramPayload(ChatRequestEvent request, string chatId)
    {
        var telegram = request.Telegram;
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = string.IsNullOrWhiteSpace(telegram?.Text)
                ? request.Prompt ?? string.Empty
                : telegram.Text.Trim(),
        };

        if (telegram?.MessageThreadId > 0)
            payload["message_thread_id"] = telegram.MessageThreadId;

        var parseMode = Normalize(telegram?.ParseMode);
        if (!string.IsNullOrWhiteSpace(parseMode))
            payload["parse_mode"] = parseMode;

        if (telegram?.HasDisableWebPagePreview == true)
            payload["disable_web_page_preview"] = telegram.DisableWebPagePreview;

        if (telegram?.ReplyToMessageId > 0)
            payload["reply_to_message_id"] = telegram.ReplyToMessageId;

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
        await PublishSuccessAsync(request.SessionId, content, request.Telegram?.EmitChatResponse == true);
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

    private static string ResolveOperation(TelegramBridgeRequest? telegram)
    {
        return telegram?.Operation switch
        {
            TelegramBridgeOperation.WaitReply => WaitReplyOperation,
            TelegramBridgeOperation.EnsureLogin => "/ensureLogin",
            TelegramBridgeOperation.SendMessage => "/sendMessage",
            _ => string.Empty,
        };
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

}
