using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Task-scoped Telegram wait-reply agent.
/// </summary>
// Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
//   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
//   New principle: New task-scoped TelegramWaitReplyGAgent owns protobuf wait state; typed self-events advance one bounded poll per actor turn and resume bridge via WaitReplyCompleted/Failed.
public sealed class TelegramWaitReplyGAgent : GAgentBase<TelegramWaitReplyState>
{
    private const int MaxPollTimeoutSeconds = 25;
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly TimeProvider _timeProvider;

    public TelegramWaitReplyGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry)
        : this(runtime, connectorRegistry, TimeProvider.System)
    {
    }

    public TelegramWaitReplyGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry,
        TimeProvider timeProvider)
    {
        _ = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleWaitForReply(TelegramWaitForReplyCommand command)
    {
        // Refactor (iter25/cluster-027-telegram-wait-reply-actor-turn):
        //   Old pattern: Telegram bridge maintains in-process wait-reply state in dict; bridge owns wait + reply lifecycle inline
        //   New principle: New task-scoped TelegramWaitReplyGAgent owns protobuf wait state; typed self-events advance one bounded poll per actor turn and resume bridge via WaitReplyCompleted/Failed.
        ArgumentNullException.ThrowIfNull(command);

        var generation = State.Generation + 1;
        var state = BuildStartedState(command, generation);
        await PersistDomainEventAsync(new TelegramWaitReplyStartedEvent { State = state });

        if (!_connectorRegistry.TryGet(command.ConnectorName, out var connector) || connector == null)
        {
            await CompleteFailureAsync($"telegram connector '{command.ConnectorName}' not found");
            return;
        }

        if (State.StartFromLatest && !State.HasNextOffset)
        {
            await SendToAsync(Id, new TelegramWaitReplyBootstrapDueEvent
            {
                CommandId = State.CommandId,
                Generation = State.Generation,
            });
            return;
        }

        await SendToAsync(Id, new TelegramWaitReplyPollDueEvent
        {
            CommandId = State.CommandId,
            Generation = State.Generation,
        });
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleBootstrapDue(TelegramWaitReplyBootstrapDueEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!IsActiveContinuation(evt.CommandId, evt.Generation))
            return;

        if (!_connectorRegistry.TryGet(State.ConnectorName, out var connector) || connector == null)
        {
            await CompleteFailureAsync($"telegram connector '{State.ConnectorName}' not found");
            return;
        }

        var bootstrap = await ExecuteGetUpdatesAsync(offset: null, pollTimeoutSeconds: 0, perCallTimeoutMs: 5_000, connector);
        if (!bootstrap.Success)
        {
            await CompleteFailureAsync(string.IsNullOrWhiteSpace(bootstrap.Error)
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
            await CompleteFailureAsync($"telegram bootstrap parse failed: {bootstrapError}");
            return;
        }

        var bootstrapRecentCutoffUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds()
            - Math.Max(30, Math.Min(600, State.WaitTimeoutMs / 1000 + 10));
        var matchedUpdates = SelectMatchedUpdates(
            bootstrapUpdates,
            minimumUpdateId: null,
            minimumDateUnixExclusive: bootstrapRecentCutoffUnix);

        if (matchedUpdates.Count > 0 && !State.CollectAllReplies)
        {
            await CompleteSuccessAsync(matchedUpdates[^1].Content);
            return;
        }

        var next = State.Clone();
        if (bootstrapMaxUpdateId.HasValue)
            next.NextOffset = bootstrapMaxUpdateId.Value + 1;
        ApplyMatches(next, matchedUpdates);

        var result = ResolveCurrentResult(next, emptyPoll: matchedUpdates.Count == 0);
        if (result.HasValue)
        {
            await CompleteResultAsync(result.Value);
            return;
        }

        await PersistAndContinuePollAsync(next);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandlePollDue(TelegramWaitReplyPollDueEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!IsActiveContinuation(evt.CommandId, evt.Generation))
            return;

        if (!_connectorRegistry.TryGet(State.ConnectorName, out var connector) || connector == null)
        {
            await CompleteFailureAsync($"telegram connector '{State.ConnectorName}' not found");
            return;
        }

        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= State.DeadlineUnixMs)
        {
            await CompleteTimeoutAsync();
            return;
        }

        var currentPollSeconds = ResolveCurrentPollSeconds();
        long? requestedOffset = State.HasNextOffset ? State.NextOffset : null;
        var poll = await ExecuteGetUpdatesAsync(
            requestedOffset,
            currentPollSeconds,
            perCallTimeoutMs: (currentPollSeconds + 3) * 1_000,
            connector);
        if (!poll.Success)
        {
            await CompleteFailureAsync(string.IsNullOrWhiteSpace(poll.Error)
                ? "telegram getUpdates failed"
                : poll.Error.Trim());
            return;
        }

        if (!TryParseTelegramUpdates(poll.Output, out var updates, out var maxUpdateId, out var parseError))
        {
            await CompleteFailureAsync($"telegram getUpdates parse failed: {parseError}");
            return;
        }

        var matchedUpdates = SelectMatchedUpdates(
            updates,
            minimumUpdateId: requestedOffset,
            minimumDateUnixExclusive: null);
        var next = State.Clone();
        if (maxUpdateId.HasValue)
            next.NextOffset = maxUpdateId.Value + 1;
        ApplyMatches(next, matchedUpdates);

        var result = ResolveCurrentResult(next, emptyPoll: matchedUpdates.Count == 0);
        if (result.HasValue)
        {
            await CompleteResultAsync(result.Value);
            return;
        }

        await PersistAndContinuePollAsync(next);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTimeoutDue(TelegramWaitReplyTimeoutDueEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!IsActiveContinuation(evt.CommandId, evt.Generation))
            return;

        await CompleteTimeoutAsync();
    }

    protected override TelegramWaitReplyState TransitionState(TelegramWaitReplyState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<TelegramWaitReplyStartedEvent>((_, started) => started.State.Clone())
            .On<TelegramWaitReplyProgressedEvent>((_, progressed) => progressed.State.Clone())
            .On<TelegramWaitReplyClearedEvent>((state, cleared) =>
            {
                if (string.Equals(state.CommandId, cleared.CommandId, StringComparison.Ordinal) &&
                    state.Generation == cleared.Generation)
                {
                    var next = state.Clone();
                    next.Active = false;
                    next.PendingMatchedUpdate = null;
                    next.CollectedReplies.Clear();
                    next.CollectedReplyOrder.Clear();
                    return next;
                }

                return state;
            })
            .OrCurrent();
    }

    private TelegramWaitReplyState BuildStartedState(TelegramWaitForReplyCommand command, long generation)
    {
        var state = new TelegramWaitReplyState
        {
            Active = true,
            Generation = generation,
            CommandId = command.CommandId,
            SessionId = command.SessionId,
            ConnectorName = command.ConnectorName,
            ExpectedChatId = command.ExpectedChatId,
            ExpectedFromUserId = command.ExpectedFromUserId,
            ExpectedFromUsername = command.ExpectedFromUsername,
            CorrelationContains = command.CorrelationContains,
            WaitTimeoutMs = command.WaitTimeoutMs,
            PollTimeoutSeconds = command.PollTimeoutSeconds,
            SettlePollsAfterMatch = command.SettlePollsAfterMatch,
            CollectAllReplies = command.CollectAllReplies,
            StartFromLatest = command.StartFromLatest,
            EmitChatResponse = command.EmitChatResponse,
            DeadlineUnixMs = _timeProvider.GetUtcNow().AddMilliseconds(command.WaitTimeoutMs).ToUnixTimeMilliseconds(),
        };
        state.ConnectorParameters.Add(command.ConnectorParameters);
        if (command.HasOffset)
            state.NextOffset = command.Offset;
        return state;
    }

    private bool IsActiveContinuation(string commandId, long generation) =>
        State.Active &&
        State.Generation == generation &&
        string.Equals(State.CommandId, commandId, StringComparison.Ordinal);

    private async Task PersistAndContinuePollAsync(TelegramWaitReplyState next)
    {
        await PersistDomainEventAsync(new TelegramWaitReplyProgressedEvent { State = next });
        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= State.DeadlineUnixMs)
        {
            await CompleteTimeoutAsync();
            return;
        }

        await SendToAsync(Id, new TelegramWaitReplyPollDueEvent
        {
            CommandId = State.CommandId,
            Generation = State.Generation,
        });
    }

    private async Task CompleteTimeoutAsync()
    {
        if (State.PendingMatchedUpdate != null)
        {
            await CompleteSuccessAsync(State.CollectAllReplies
                ? BuildMatchedReplyContent()
                : State.PendingMatchedUpdate.Content);
            return;
        }

        await CompleteFailureAsync(
            $"telegram group stream timeout after {State.WaitTimeoutMs}ms without matched reply");
    }

    private Task CompleteResultAsync(TelegramWaitReplyResult result) =>
        result.Success ? CompleteSuccessAsync(result.Content) : CompleteFailureAsync(result.Error);

    private async Task CompleteSuccessAsync(string content)
    {
        await PublishAsync(
            new TelegramWaitReplyCompletedEvent
            {
                CommandId = State.CommandId,
                SessionId = State.SessionId,
                Content = content,
                EmitChatResponse = State.EmitChatResponse,
                WaitActorId = Id,
            },
            TopologyAudience.Parent);
        await ClearActiveStateAsync();
    }

    private async Task CompleteFailureAsync(string error)
    {
        await PublishAsync(
            new TelegramWaitReplyFailedEvent
            {
                CommandId = State.CommandId,
                SessionId = State.SessionId,
                Error = error,
                EmitChatResponse = State.EmitChatResponse,
                WaitActorId = Id,
            },
            TopologyAudience.Parent);
        await ClearActiveStateAsync();
    }

    private Task ClearActiveStateAsync()
    {
        if (!State.Active)
            return Task.CompletedTask;

        return PersistDomainEventAsync(new TelegramWaitReplyClearedEvent
        {
            CommandId = State.CommandId,
            Generation = State.Generation,
        });
    }

    private int ResolveCurrentPollSeconds()
    {
        var remainingMs = State.DeadlineUnixMs - _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var remainingSeconds = (int)Math.Ceiling(Math.Max(1, remainingMs / 1000.0));
        var currentPollMaxSeconds = State.PendingMatchedUpdate != null ? 1 : State.PollTimeoutSeconds;
        return Math.Clamp(remainingSeconds, 1, currentPollMaxSeconds);
    }

    private List<TelegramInboundUpdate> SelectMatchedUpdates(
        IEnumerable<TelegramInboundUpdate> updates,
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

            if (!IsMatchedUpdate(update))
                continue;

            matches.Add(update);
        }

        return matches;
    }

    private bool IsMatchedUpdate(TelegramInboundUpdate update)
    {
        if (!string.Equals(update.ChatId, State.ExpectedChatId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(State.ExpectedFromUserId) &&
            !string.Equals(update.FromUserId, State.ExpectedFromUserId, StringComparison.Ordinal))
        {
            return false;
        }

        // Some Telegram update variants omit username; keep other guards authoritative.
        if (!string.IsNullOrWhiteSpace(State.ExpectedFromUsername))
        {
            var actualUsername = NormalizeUsername(update.FromUsername);
            if (!string.IsNullOrWhiteSpace(actualUsername) &&
                !string.Equals(actualUsername, State.ExpectedFromUsername, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(update.Content))
            return false;

        return string.IsNullOrWhiteSpace(State.CorrelationContains) ||
               update.Content.IndexOf(State.CorrelationContains, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ApplyMatches(TelegramWaitReplyState state, IReadOnlyList<TelegramInboundUpdate> matchedUpdates)
    {
        if (matchedUpdates.Count == 0)
            return;

        state.PendingMatchedUpdate = ToState(matchedUpdates[^1]);
        state.PollsSinceLastMatch = 0;
        if (!state.CollectAllReplies)
            return;

        foreach (var update in matchedUpdates)
        {
            var identity = BuildMatchedReplyIdentity(update);
            if (!state.CollectedReplies.ContainsKey(identity))
                state.CollectedReplyOrder.Add(identity);
            state.CollectedReplies[identity] = ToState(update);
        }
    }

    private TelegramWaitReplyResult? ResolveCurrentResult(TelegramWaitReplyState state, bool emptyPoll)
    {
        if (state.PendingMatchedUpdate == null)
            return null;

        if (emptyPoll)
            state.PollsSinceLastMatch++;

        if (!emptyPoll || state.PollsSinceLastMatch < state.SettlePollsAfterMatch)
            return state.SettlePollsAfterMatch <= 0 ? BuildCurrentSuccess(state) : null;

        return BuildCurrentSuccess(state);
    }

    private TelegramWaitReplyResult BuildCurrentSuccess(TelegramWaitReplyState state)
    {
        if (!state.CollectAllReplies)
            return TelegramWaitReplyResult.Ok(state.PendingMatchedUpdate?.Content ?? string.Empty);

        return TelegramWaitReplyResult.Ok(BuildMatchedReplyContent(state));
    }

    private string BuildMatchedReplyContent() => BuildMatchedReplyContent(State);

    private static string BuildMatchedReplyContent(TelegramWaitReplyState state)
    {
        if (state.CollectedReplies.Count == 0 || state.CollectedReplyOrder.Count == 0)
            return state.PendingMatchedUpdate?.Content ?? string.Empty;

        var orderedReplies = new List<string>(state.CollectedReplyOrder.Count);
        foreach (var identity in state.CollectedReplyOrder)
        {
            if (!state.CollectedReplies.TryGetValue(identity, out var update))
                continue;
            if (string.IsNullOrWhiteSpace(update.Content))
                continue;

            orderedReplies.Add(update.Content);
        }

        if (orderedReplies.Count == 0)
            return state.PendingMatchedUpdate?.Content ?? string.Empty;
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
        long? offset,
        int pollTimeoutSeconds,
        int perCallTimeoutMs,
        IConnector connector)
    {
        var parameters = new Dictionary<string, string>(State.ConnectorParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = "POST",
            ["content_type"] = "application/json",
            ["timeout_ms"] = perCallTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var connectorRequest = new ConnectorRequest
        {
            RunId = State.CommandId,
            StepId = State.SessionId,
            Connector = State.ConnectorName,
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

    private static TelegramInboundUpdateState ToState(TelegramInboundUpdate update) =>
        new()
        {
            UpdateId = update.UpdateId,
            MessageId = update.MessageId,
            DateUnix = update.DateUnix,
            ChatId = update.ChatId,
            FromUserId = update.FromUserId,
            FromUsername = update.FromUsername,
            Content = update.Content,
        };

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
