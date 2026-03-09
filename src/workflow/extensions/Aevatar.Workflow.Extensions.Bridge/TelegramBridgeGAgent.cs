using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Telegram channel bridge agent.
/// Handles ChatRequestEvent -> Telegram sendMessage and waitReply polling.
/// </summary>
public class TelegramBridgeGAgent : GAgentBase
{
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string WaitReplyOperation = "/waitReply";
    private const int DefaultWaitReplyTimeoutMs = 120_000;
    private const int DefaultConnectorExecutionWatchdogMs = 20_000;
    private const int DefaultPollTimeoutSeconds = 8;
    private const int DefaultSettlePollsAfterMatch = 1;
    private const int MaxSettlePollsAfterMatch = 5;
    private const int MaxPollTimeoutSeconds = 25;
    private readonly IConnectorRegistry _connectorRegistry;

    protected virtual string DefaultConnectorName => "telegram";

    public TelegramBridgeGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry)
    {
        _ = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectorName = ReadMetadata(request.Metadata, "telegram.connector", "connector", "connector_name");
        if (string.IsNullOrWhiteSpace(connectorName))
            connectorName = DefaultConnectorName;

        if (!_connectorRegistry.TryGet(connectorName, out var connector) || connector == null)
        {
            await PublishFailureAsync(request, $"telegram connector '{connectorName}' not found");
            return;
        }

        var chatId = ReadMetadata(request.Metadata, "telegram.chat_id", "chat_id");
        if (string.IsNullOrWhiteSpace(chatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required");
            return;
        }

        var operation = ReadMetadata(request.Metadata, "telegram.operation", "operation", "path");
        if (string.IsNullOrWhiteSpace(operation))
            operation = "/sendMessage";

        if (string.Equals(operation, WaitReplyOperation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "wait_reply", StringComparison.OrdinalIgnoreCase))
        {
            await HandleWaitReplyAsync(request, connectorName, connector);
            return;
        }

        var requestPayload = BuildTelegramPayload(request, chatId.Trim());
        var connectorParameters = BuildConnectorParameters(request.Metadata);
        var connectorRequest = new ConnectorRequest
        {
            RunId = ReadMetadata(request.Metadata, "run_id", "workflow.run_id", "workflow_run_id", "session_id"),
            StepId = ReadMetadata(request.Metadata, "step_id", "workflow.step_id", "workflow_step_id"),
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
        string connectorName,
        IConnector connector)
    {
        var expectedChatId = ReadMetadata(request.Metadata, "telegram.chat_id", "chat_id").Trim();
        if (string.IsNullOrWhiteSpace(expectedChatId))
        {
            await PublishFailureAsync(request, "telegram metadata 'chat_id' is required for /waitReply");
            return;
        }

        var expectedFromUserId = ReadMetadata(
            request.Metadata,
            "telegram.expected_from_user_id",
            "expected_from_user_id",
            "from_user_id").Trim();
        var expectedFromUsername = NormalizeUsername(ReadMetadata(
            request.Metadata,
            "telegram.expected_from_username",
            "expected_from_username",
            "from_username",
            "from_user"));
        var correlationContains = ReadMetadata(
            request.Metadata,
            "telegram.correlation_contains",
            "correlation_contains",
            "contains").Trim();

        var waitTimeoutMs = ResolveWaitReplyTimeoutMs(request.Metadata);
        var pollTimeoutSeconds = ResolvePollTimeoutSeconds(request.Metadata);
        var settlePollsAfterMatch = ResolveSettlePollsAfterMatch(request.Metadata);
        var collectAllReplies = ResolveCollectAllReplies(request.Metadata);
        var startFromLatest = ResolveStartFromLatest(request.Metadata);
        var bootstrapRecentCutoffUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            - Math.Max(30, Math.Min(600, waitTimeoutMs / 1000 + 10));

        var connectorParameters = BuildConnectorParameters(request.Metadata);
        long? offset = TryReadInt64(
            ReadMetadata(request.Metadata, "telegram.offset", "offset"),
            minimum: 0);
        var collectedByIdentity = collectAllReplies
            ? new Dictionary<string, TelegramInboundUpdate>(StringComparer.Ordinal)
            : null;
        var collectedIdentityOrder = collectAllReplies
            ? new List<string>()
            : null;
        TelegramInboundUpdate? pendingMatchedUpdate = null;
        var pollsSinceLastMatch = 0;

        if (startFromLatest && offset == null)
        {
            var bootstrap = await ExecuteGetUpdatesAsync(
                request,
                connectorName,
                connector,
                connectorParameters,
                offset: null,
                pollTimeoutSeconds: 0,
                perCallTimeoutMs: 5_000);
            if (!bootstrap.Success)
            {
                await PublishFailureAsync(
                    request,
                    string.IsNullOrWhiteSpace(bootstrap.Error)
                        ? "telegram bootstrap getUpdates failed"
                        : bootstrap.Error.Trim());
                return;
            }

            if (!TryParseTelegramUpdates(
                bootstrap.Output,
                out var bootstrapUpdates,
                out var bootstrapMaxUpdateId,
                out var bootstrapError))
            {
                await PublishFailureAsync(request, $"telegram bootstrap parse failed: {bootstrapError}");
                return;
            }

            var bootstrapMatchedUpdates = SelectMatchedUpdates(
                bootstrapUpdates,
                expectedChatId,
                expectedFromUserId,
                expectedFromUsername,
                correlationContains,
                minimumUpdateId: null,
                minimumDateUnixExclusive: bootstrapRecentCutoffUnix);
            if (bootstrapMatchedUpdates.Count > 0)
            {
                pendingMatchedUpdate = bootstrapMatchedUpdates[^1];
                pollsSinceLastMatch = 0;
                if (collectAllReplies)
                {
                    MergeMatchedUpdates(
                        bootstrapMatchedUpdates,
                        collectedByIdentity!,
                        collectedIdentityOrder!);
                    if (settlePollsAfterMatch <= 0)
                    {
                        await PublishSuccessAsync(
                            request,
                            BuildMatchedReplyContent(
                                collectedByIdentity!,
                                collectedIdentityOrder!,
                                pendingMatchedUpdate));
                        return;
                    }
                }
                else
                {
                    await PublishSuccessAsync(request, pendingMatchedUpdate.Content);
                    return;
                }
            }

            if (bootstrapMaxUpdateId.HasValue)
                offset = bootstrapMaxUpdateId.Value + 1;
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(waitTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var currentPollMaxSeconds = pendingMatchedUpdate != null ? 1 : pollTimeoutSeconds;
            var currentPollSeconds = Math.Clamp(
                (int)Math.Ceiling(Math.Max(1, remaining.TotalSeconds)),
                1,
                currentPollMaxSeconds);
            var perCallTimeoutMs = (currentPollSeconds + 3) * 1_000;
            var requestedOffset = offset;

            var poll = await ExecuteGetUpdatesAsync(
                request,
                connectorName,
                connector,
                connectorParameters,
                offset,
                currentPollSeconds,
                perCallTimeoutMs);
            if (!poll.Success)
            {
                await PublishFailureAsync(
                    request,
                    string.IsNullOrWhiteSpace(poll.Error)
                        ? "telegram getUpdates failed"
                        : poll.Error.Trim());
                return;
            }

            if (!TryParseTelegramUpdates(poll.Output, out var updates, out var maxUpdateId, out var parseError))
            {
                await PublishFailureAsync(request, $"telegram getUpdates parse failed: {parseError}");
                return;
            }

            if (maxUpdateId.HasValue)
                offset = maxUpdateId.Value + 1;

            var matchedUpdatesInBatch = SelectMatchedUpdates(
                updates,
                expectedChatId,
                expectedFromUserId,
                expectedFromUsername,
                correlationContains,
                minimumUpdateId: requestedOffset,
                minimumDateUnixExclusive: null);
            if (matchedUpdatesInBatch.Count > 0)
            {
                var latestMatchedInBatch = matchedUpdatesInBatch[^1];
                pendingMatchedUpdate = latestMatchedInBatch;
                pollsSinceLastMatch = 0;
                if (collectAllReplies)
                {
                    MergeMatchedUpdates(
                        matchedUpdatesInBatch,
                        collectedByIdentity!,
                        collectedIdentityOrder!);
                }
                if (settlePollsAfterMatch <= 0)
                {
                    await PublishSuccessAsync(
                        request,
                        collectAllReplies
                            ? BuildMatchedReplyContent(
                                collectedByIdentity!,
                                collectedIdentityOrder!,
                                pendingMatchedUpdate)
                            : pendingMatchedUpdate.Content);
                    return;
                }

                continue;
            }

            if (pendingMatchedUpdate != null)
            {
                pollsSinceLastMatch++;
                if (pollsSinceLastMatch >= settlePollsAfterMatch)
                {
                    await PublishSuccessAsync(
                        request,
                        collectAllReplies
                            ? BuildMatchedReplyContent(
                                collectedByIdentity!,
                                collectedIdentityOrder!,
                                pendingMatchedUpdate)
                            : pendingMatchedUpdate.Content);
                    return;
                }
            }
        }

        if (pendingMatchedUpdate != null)
        {
            await PublishSuccessAsync(
                request,
                collectAllReplies
                    ? BuildMatchedReplyContent(
                        collectedByIdentity!,
                        collectedIdentityOrder!,
                        pendingMatchedUpdate)
                    : pendingMatchedUpdate.Content);
            return;
        }

        await PublishFailureAsync(
            request,
            $"telegram group stream timeout after {waitTimeoutMs}ms without matched reply");
    }

    private static List<TelegramInboundUpdate> SelectMatchedUpdates(
        IEnumerable<TelegramInboundUpdate> updates,
        string expectedChatId,
        string expectedFromUserId,
        string expectedFromUsername,
        string correlationContains,
        long? minimumUpdateId,
        long? minimumDateUnixExclusive)
    {
        var matches = new List<TelegramInboundUpdate>();
        foreach (var update in updates)
        {
            if (minimumUpdateId.HasValue &&
                update.UpdateId >= 0 &&
                update.UpdateId < minimumUpdateId.Value)
            {
                continue;
            }

            if (minimumDateUnixExclusive.HasValue &&
                update.DateUnix > 0 &&
                update.DateUnix < minimumDateUnixExclusive.Value)
            {
                continue;
            }

            if (!IsMatchedUpdate(
                    update,
                    expectedChatId,
                    expectedFromUserId,
                    expectedFromUsername,
                    correlationContains))
            {
                continue;
            }

            matches.Add(update);
        }

        return matches;
    }

    private static void MergeMatchedUpdates(
        IEnumerable<TelegramInboundUpdate> matchedUpdates,
        IDictionary<string, TelegramInboundUpdate> latestByIdentity,
        IList<string> identityOrder)
    {
        foreach (var update in matchedUpdates)
        {
            var identity = BuildMatchedReplyIdentity(update);
            if (!latestByIdentity.ContainsKey(identity))
                identityOrder.Add(identity);
            latestByIdentity[identity] = update;
        }
    }

    private static string BuildMatchedReplyContent(
        IReadOnlyDictionary<string, TelegramInboundUpdate> latestByIdentity,
        IReadOnlyList<string> identityOrder,
        TelegramInboundUpdate? fallback)
    {
        if (latestByIdentity.Count == 0 || identityOrder.Count == 0)
            return fallback?.Content ?? string.Empty;

        var orderedReplies = new List<string>(identityOrder.Count);
        foreach (var identity in identityOrder)
        {
            if (!latestByIdentity.TryGetValue(identity, out var update))
                continue;
            if (string.IsNullOrWhiteSpace(update.Content))
                continue;

            orderedReplies.Add(update.Content);
        }

        if (orderedReplies.Count == 0)
            return fallback?.Content ?? string.Empty;
        if (orderedReplies.Count == 1)
            return orderedReplies[0];

        return string.Join("\n\n---\n\n", orderedReplies);
    }

    private static string BuildMatchedReplyIdentity(TelegramInboundUpdate update)
    {
        if (update.MessageId > 0)
            return $"msg:{update.ChatId}:{update.MessageId}";
        if (update.UpdateId >= 0)
            return $"update:{update.UpdateId}";

        return $"raw:{update.ChatId}:{update.FromUserId}:{update.DateUnix}:{update.Content}";
    }

    private async Task<ConnectorResponse> ExecuteGetUpdatesAsync(
        ChatRequestEvent request,
        string connectorName,
        IConnector connector,
        IReadOnlyDictionary<string, string> baseParameters,
        long? offset,
        int pollTimeoutSeconds,
        int perCallTimeoutMs)
    {
        var parameters = new Dictionary<string, string>(baseParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = "POST",
            ["content_type"] = "application/json",
            ["timeout_ms"] = perCallTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var connectorRequest = new ConnectorRequest
        {
            RunId = ReadMetadata(request.Metadata, "run_id", "workflow.run_id", "workflow_run_id", "session_id"),
            StepId = ReadMetadata(request.Metadata, "step_id", "workflow.step_id", "workflow_step_id"),
            Connector = connectorName,
            Operation = "/getUpdates",
            Payload = BuildGetUpdatesPayload(offset, pollTimeoutSeconds),
            Parameters = parameters,
        };

        return await ExecuteConnectorWithWatchdogAsync(
            connector,
            connectorRequest,
            ResolveConnectorExecutionWatchdogMs(parameters));
    }

    private static string BuildGetUpdatesPayload(long? offset, int pollTimeoutSeconds)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timeout"] = Math.Clamp(pollTimeoutSeconds, 0, MaxPollTimeoutSeconds),
            ["allowed_updates"] = new[] { "message", "channel_post" },
        };
        if (offset.HasValue && offset.Value >= 0)
            payload["offset"] = offset.Value;

        return JsonSerializer.Serialize(payload);
    }

    private static bool TryParseTelegramUpdates(
        string output,
        out List<TelegramInboundUpdate> updates,
        out long? maxUpdateId,
        out string error)
    {
        updates = [];
        maxUpdateId = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(output))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root is not a JSON object";
                return false;
            }

            if (root.TryGetProperty("ok", out var okElement) &&
                okElement.ValueKind is JsonValueKind.False)
            {
                var description = root.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null;
                error = string.IsNullOrWhiteSpace(description)
                    ? "telegram api returned ok=false"
                    : description;
                return false;
            }

            if (!root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var item in result.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var updateId = TryGetInt64(item, "update_id");
                if (updateId.HasValue)
                    maxUpdateId = !maxUpdateId.HasValue ? updateId : Math.Max(maxUpdateId.Value, updateId.Value);

                JsonElement message;
                if (item.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object)
                {
                    message = messageValue;
                }
                else if (item.TryGetProperty("channel_post", out var channelPost) && channelPost.ValueKind == JsonValueKind.Object)
                {
                    message = channelPost;
                }
                else
                {
                    continue;
                }

                var chatId = TryGetNestedStringOrNumber(message, "chat", "id");
                if (string.IsNullOrWhiteSpace(chatId))
                    continue;

                var text = TryGetString(message, "text");
                if (string.IsNullOrWhiteSpace(text))
                    text = TryGetString(message, "caption");

                var messageId = TryGetInt64(message, "message_id") ?? 0;
                var dateUnix = TryGetInt64(message, "date") ?? 0;
                var fromUserId = TryGetNestedStringOrNumber(message, "from", "id");
                var fromUsername = TryGetNestedStringOrNumber(message, "from", "username");

                updates.Add(new TelegramInboundUpdate(
                    UpdateId: updateId ?? -1,
                    MessageId: messageId,
                    DateUnix: dateUnix,
                    ChatId: chatId,
                    FromUserId: fromUserId,
                    FromUsername: fromUsername,
                    Content: text ?? string.Empty));
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsMatchedUpdate(
        TelegramInboundUpdate update,
        string expectedChatId,
        string expectedFromUserId,
        string expectedFromUsername,
        string correlationContains)
    {
        if (!string.Equals(update.ChatId, expectedChatId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(expectedFromUserId) &&
            !string.Equals(update.FromUserId, expectedFromUserId, StringComparison.Ordinal))
        {
            return false;
        }

        // Some Telegram update variants may omit username in from{}.
        // When username is present we enforce an exact match; otherwise we
        // continue with other guards (chat_id / from_user_id / correlation).
        if (!string.IsNullOrWhiteSpace(expectedFromUsername))
        {
            var actualUsername = NormalizeUsername(update.FromUsername);
            if (!string.IsNullOrWhiteSpace(actualUsername) &&
                !string.Equals(actualUsername, expectedFromUsername, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(update.Content))
            return false;

        if (!string.IsNullOrWhiteSpace(correlationContains) &&
            update.Content.IndexOf(correlationContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    private static long? TryGetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static string TryGetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static string TryGetNestedStringOrNumber(JsonElement element, string nested, string name)
    {
        if (!element.TryGetProperty(nested, out var nestedElement) ||
            nestedElement.ValueKind != JsonValueKind.Object ||
            !nestedElement.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
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

    private static int ResolveConnectorExecutionWatchdogMs(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return Math.Clamp(parsed, 100, 300_000);
        }

        return DefaultConnectorExecutionWatchdogMs;
    }

    private static async Task<ConnectorResponse> ExecuteConnectorWithWatchdogAsync(
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

        var threadId = ReadMetadata(request.Metadata, "telegram.message_thread_id", "message_thread_id");
        if (!string.IsNullOrWhiteSpace(threadId) && long.TryParse(threadId, out var parsedThreadId))
            payload["message_thread_id"] = parsedThreadId;

        var parseMode = ReadMetadata(request.Metadata, "telegram.parse_mode", "parse_mode");
        if (!string.IsNullOrWhiteSpace(parseMode))
            payload["parse_mode"] = parseMode.Trim();

        var disablePreview = ReadMetadata(
            request.Metadata,
            "telegram.disable_web_page_preview",
            "disable_web_page_preview");
        if (TryParseBool(disablePreview, out var parsedDisablePreview))
            payload["disable_web_page_preview"] = parsedDisablePreview;

        var replyToMessageId = ReadMetadata(request.Metadata, "telegram.reply_to_message_id", "reply_to_message_id");
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
        if (ShouldEmitChatResponse(request.Metadata))
        {
            await PublishAsync(
                new ChatResponseEvent
                {
                    SessionId = request.SessionId,
                    Content = content,
                },
                EventDirection.Up);
        }

        await PublishAsync(
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = content,
            },
            EventDirection.Up);
    }

    private async Task PublishFailureAsync(ChatRequestEvent request, string error)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "telegram bridge call failed" : error.Trim();
        await PublishAsync(
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = $"{LlmFailureContentPrefix} {safeError}",
            },
            EventDirection.Up);
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

    private sealed record TelegramInboundUpdate(
        long UpdateId,
        long MessageId,
        long DateUnix,
        string ChatId,
        string FromUserId,
        string FromUsername,
        string Content);
}
