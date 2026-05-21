using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Task-scoped Telegram wait-reply agent.
/// </summary>
// Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
//   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
//   New principle: New task-scoped TelegramWaitReplyGAgent owns wait state; bridge sends WaitForReplyCommand and resumes via WaitReplyCompleted/Failed event(reference lark stream actor architecture for unification)
public sealed class TelegramWaitReplyGAgent : GAgentBase
{
    private const int MaxPollTimeoutSeconds = 25;
    private readonly IConnectorRegistry _connectorRegistry;

    public TelegramWaitReplyGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry)
    {
        _ = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleWaitForReply(TelegramWaitForReplyCommand command)
    {
        // Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
        //   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
        //   New principle: New task-scoped TelegramWaitReplyGAgent owns wait state; bridge sends WaitForReplyCommand and resumes via WaitReplyCompleted/Failed event(reference lark stream actor architecture for unification)
        ArgumentNullException.ThrowIfNull(command);

        if (!_connectorRegistry.TryGet(command.ConnectorName, out var connector) || connector == null)
        {
            await PublishFailedAsync(command, $"telegram connector '{command.ConnectorName}' not found");
            return;
        }

        var result = await WaitForReplyAsync(command, connector);
        if (result.Success)
        {
            await PublishAsync(
                new TelegramWaitReplyCompletedEvent
                {
                    CommandId = command.CommandId,
                    SessionId = command.SessionId,
                    Content = result.Content,
                    EmitChatResponse = command.EmitChatResponse,
                    WaitActorId = Id,
                },
                TopologyAudience.Parent);
            return;
        }

        await PublishFailedAsync(command, result.Error);
    }

    private Task PublishFailedAsync(TelegramWaitForReplyCommand command, string error)
    {
        return PublishAsync(
            new TelegramWaitReplyFailedEvent
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Error = error,
                EmitChatResponse = command.EmitChatResponse,
                WaitActorId = Id,
            },
            TopologyAudience.Parent);
    }

    private static async Task<TelegramWaitReplyResult> WaitForReplyAsync(
        TelegramWaitForReplyCommand command,
        IConnector connector)
    {
        var context = new TelegramWaitReplyRuntimeContext(command);

        if (command.StartFromLatest && context.Offset == null)
        {
            var bootstrapResult = await BootstrapFromLatestAsync(command, connector, context);
            if (!bootstrapResult.ShouldContinue)
                return bootstrapResult.Result;
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(command.WaitTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pollResult = await PollOnceAsync(command, connector, context, deadline);
            if (!pollResult.ShouldContinue)
                return pollResult.Result;
        }

        return context.ToTerminalResult(command.WaitTimeoutMs);
    }

    private static async Task<TelegramWaitReplyStepResult> BootstrapFromLatestAsync(
        TelegramWaitForReplyCommand command,
        IConnector connector,
        TelegramWaitReplyRuntimeContext context)
    {
        var bootstrap = await ExecuteGetUpdatesAsync(
            command,
            connector,
            offset: null,
            pollTimeoutSeconds: 0,
            perCallTimeoutMs: 5_000);
        if (!bootstrap.Success)
        {
            return TelegramWaitReplyStepResult.Done(TelegramWaitReplyResult.Fail(string.IsNullOrWhiteSpace(bootstrap.Error)
                ? "telegram bootstrap getUpdates failed"
                : bootstrap.Error.Trim()));
        }

        if (!TryParseTelegramUpdates(
                bootstrap.Output,
                out var bootstrapUpdates,
                out var bootstrapMaxUpdateId,
                out var bootstrapError))
        {
            return TelegramWaitReplyStepResult.Done(
                TelegramWaitReplyResult.Fail($"telegram bootstrap parse failed: {bootstrapError}"));
        }

        var bootstrapRecentCutoffUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            - Math.Max(30, Math.Min(600, command.WaitTimeoutMs / 1000 + 10));
        var bootstrapMatchedUpdates = SelectMatchedUpdates(
            bootstrapUpdates,
            command,
            minimumUpdateId: null,
            minimumDateUnixExclusive: bootstrapRecentCutoffUnix);
        if (bootstrapMatchedUpdates.Count > 0 && !command.CollectAllReplies)
        {
            return TelegramWaitReplyStepResult.Done(
                TelegramWaitReplyResult.Ok(bootstrapMatchedUpdates[^1].Content));
        }

        var matchedResult = context.ApplyMatches(command, bootstrapMatchedUpdates);
        if (matchedResult.HasValue)
            return TelegramWaitReplyStepResult.Done(matchedResult.Value);

        if (bootstrapMaxUpdateId.HasValue)
            context.Offset = bootstrapMaxUpdateId.Value + 1;
        return TelegramWaitReplyStepResult.Continue();
    }

    private static async Task<TelegramWaitReplyStepResult> PollOnceAsync(
        TelegramWaitForReplyCommand command,
        IConnector connector,
        TelegramWaitReplyRuntimeContext context,
        DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        var currentPollMaxSeconds = context.HasPendingMatch ? 1 : command.PollTimeoutSeconds;
        var currentPollSeconds = Math.Clamp(
            (int)Math.Ceiling(Math.Max(1, remaining.TotalSeconds)),
            1,
            currentPollMaxSeconds);
        var perCallTimeoutMs = (currentPollSeconds + 3) * 1_000;
        var requestedOffset = context.Offset;

        var poll = await ExecuteGetUpdatesAsync(
            command,
            connector,
            context.Offset,
            currentPollSeconds,
            perCallTimeoutMs);
        if (!poll.Success)
        {
            return TelegramWaitReplyStepResult.Done(TelegramWaitReplyResult.Fail(string.IsNullOrWhiteSpace(poll.Error)
                ? "telegram getUpdates failed"
                : poll.Error.Trim()));
        }

        if (!TryParseTelegramUpdates(poll.Output, out var updates, out var maxUpdateId, out var parseError))
        {
            return TelegramWaitReplyStepResult.Done(
                TelegramWaitReplyResult.Fail($"telegram getUpdates parse failed: {parseError}"));
        }

        if (maxUpdateId.HasValue)
            context.Offset = maxUpdateId.Value + 1;

        var matchedUpdatesInBatch = SelectMatchedUpdates(
            updates,
            command,
            minimumUpdateId: requestedOffset,
            minimumDateUnixExclusive: null);
        var matchedResult = context.ApplyMatches(command, matchedUpdatesInBatch);
        if (matchedResult.HasValue)
            return TelegramWaitReplyStepResult.Done(matchedResult.Value);

        return TelegramWaitReplyStepResult.Continue();
    }

    private static List<TelegramInboundUpdate> SelectMatchedUpdates(
        IEnumerable<TelegramInboundUpdate> updates,
        TelegramWaitForReplyCommand command,
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

            if (!IsMatchedUpdate(update, command))
                continue;

            matches.Add(update);
        }

        return matches;
    }

    private static bool IsMatchedUpdate(TelegramInboundUpdate update, TelegramWaitForReplyCommand command)
    {
        if (!string.Equals(update.ChatId, command.ExpectedChatId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(command.ExpectedFromUserId) &&
            !string.Equals(update.FromUserId, command.ExpectedFromUserId, StringComparison.Ordinal))
        {
            return false;
        }

        // Some Telegram update variants omit username; keep other guards authoritative.
        if (!string.IsNullOrWhiteSpace(command.ExpectedFromUsername))
        {
            var actualUsername = NormalizeUsername(update.FromUsername);
            if (!string.IsNullOrWhiteSpace(actualUsername) &&
                !string.Equals(actualUsername, command.ExpectedFromUsername, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(update.Content))
            return false;

        return string.IsNullOrWhiteSpace(command.CorrelationContains) ||
               update.Content.IndexOf(command.CorrelationContains, StringComparison.OrdinalIgnoreCase) >= 0;
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

    private static async Task<ConnectorResponse> ExecuteGetUpdatesAsync(
        TelegramWaitForReplyCommand command,
        IConnector connector,
        long? offset,
        int pollTimeoutSeconds,
        int perCallTimeoutMs)
    {
        var parameters = new Dictionary<string, string>(command.ConnectorParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = "POST",
            ["content_type"] = "application/json",
            ["timeout_ms"] = perCallTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var connectorRequest = new ConnectorRequest
        {
            RunId = command.CommandId,
            StepId = command.SessionId,
            Connector = command.ConnectorName,
            Operation = "/getUpdates",
            Payload = BuildGetUpdatesPayload(offset, pollTimeoutSeconds),
            Parameters = parameters,
        };

        return await TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync(
            connector,
            connectorRequest,
            TelegramBridgeGAgent.ResolveConnectorExecutionWatchdogMs(parameters));
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
                AddTelegramUpdate(item, updates, ref maxUpdateId);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void AddTelegramUpdate(JsonElement item, List<TelegramInboundUpdate> updates, ref long? maxUpdateId)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        var updateId = TryGetInt64(item, "update_id");
        if (updateId.HasValue)
            maxUpdateId = !maxUpdateId.HasValue ? updateId : Math.Max(maxUpdateId.Value, updateId.Value);

        if (!TryGetMessageElement(item, out var message))
            return;

        var chatId = TryGetNestedStringOrNumber(message, "chat", "id");
        if (string.IsNullOrWhiteSpace(chatId))
            return;

        var text = TryGetString(message, "text");
        if (string.IsNullOrWhiteSpace(text))
            text = TryGetString(message, "caption");

        updates.Add(new TelegramInboundUpdate(
            UpdateId: updateId ?? -1,
            MessageId: TryGetInt64(message, "message_id") ?? 0,
            DateUnix: TryGetInt64(message, "date") ?? 0,
            ChatId: chatId,
            FromUserId: TryGetNestedStringOrNumber(message, "from", "id"),
            FromUsername: TryGetNestedStringOrNumber(message, "from", "username"),
            Content: text ?? string.Empty));
    }

    private static bool TryGetMessageElement(JsonElement item, out JsonElement message)
    {
        if (item.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object)
        {
            message = messageValue;
            return true;
        }

        if (item.TryGetProperty("channel_post", out var channelPost) && channelPost.ValueKind == JsonValueKind.Object)
        {
            message = channelPost;
            return true;
        }

        message = default;
        return false;
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

    private static string NormalizeUsername(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var normalized = raw.Trim();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    private sealed class TelegramWaitReplyRuntimeContext
    {
        private readonly Dictionary<string, TelegramInboundUpdate>? _collectedByIdentity;
        private readonly List<string>? _collectedIdentityOrder;
        private TelegramInboundUpdate? _pendingMatchedUpdate;
        private int _pollsSinceLastMatch;

        public TelegramWaitReplyRuntimeContext(TelegramWaitForReplyCommand command)
        {
            Offset = command.HasOffset ? command.Offset : null;
            if (!command.CollectAllReplies)
                return;

            _collectedByIdentity = new Dictionary<string, TelegramInboundUpdate>(StringComparer.Ordinal);
            _collectedIdentityOrder = [];
        }

        public long? Offset { get; set; }
        public bool HasPendingMatch => _pendingMatchedUpdate != null;

        public TelegramWaitReplyResult? ApplyMatches(
            TelegramWaitForReplyCommand command,
            IReadOnlyList<TelegramInboundUpdate> matchedUpdates)
        {
            if (matchedUpdates.Count == 0)
                return ApplyEmptyPoll(command);

            _pendingMatchedUpdate = matchedUpdates[^1];
            _pollsSinceLastMatch = 0;
            if (command.CollectAllReplies)
                MergeMatchedUpdates(matchedUpdates, _collectedByIdentity!, _collectedIdentityOrder!);

            return command.SettlePollsAfterMatch <= 0
                ? BuildCurrentSuccess(command)
                : null;
        }

        public TelegramWaitReplyResult ToTerminalResult(int waitTimeoutMs)
        {
            if (_pendingMatchedUpdate == null)
                return TelegramWaitReplyResult.Fail(
                    $"telegram group stream timeout after {waitTimeoutMs}ms without matched reply");

            return TelegramWaitReplyResult.Ok(_pendingMatchedUpdate.Content);
        }

        private TelegramWaitReplyResult? ApplyEmptyPoll(TelegramWaitForReplyCommand command)
        {
            if (_pendingMatchedUpdate == null)
                return null;

            _pollsSinceLastMatch++;
            return _pollsSinceLastMatch >= command.SettlePollsAfterMatch
                ? BuildCurrentSuccess(command)
                : null;
        }

        private TelegramWaitReplyResult BuildCurrentSuccess(TelegramWaitForReplyCommand command)
        {
            if (!command.CollectAllReplies)
                return TelegramWaitReplyResult.Ok(_pendingMatchedUpdate?.Content ?? string.Empty);

            return TelegramWaitReplyResult.Ok(BuildMatchedReplyContent(
                _collectedByIdentity!,
                _collectedIdentityOrder!,
                _pendingMatchedUpdate));
        }
    }

    private readonly record struct TelegramWaitReplyStepResult(
        bool ShouldContinue,
        TelegramWaitReplyResult Result)
    {
        public static TelegramWaitReplyStepResult Continue() => new(true, default);
        public static TelegramWaitReplyStepResult Done(TelegramWaitReplyResult result) => new(false, result);
    }

    private readonly record struct TelegramWaitReplyResult(bool Success, string Content, string Error)
    {
        public static TelegramWaitReplyResult Ok(string content) => new(true, content, string.Empty);
        public static TelegramWaitReplyResult Fail(string error) => new(false, string.Empty, error);
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
