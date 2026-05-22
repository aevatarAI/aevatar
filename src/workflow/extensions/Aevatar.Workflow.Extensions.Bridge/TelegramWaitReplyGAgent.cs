using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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
// Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
//   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
//   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
[GAgent("workflow.telegram-wait-reply")]
// Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
//   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
//   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
public sealed class TelegramWaitReplyGAgent : GAgentBase<TelegramWaitReplyState>, IExternalLinkAware
{
    private const string GetUpdatesLinkId = "telegram-get-updates";
    private const string GetUpdatesTimeoutCallbackPrefix = "telegram-get-updates-timeout";
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

    public IReadOnlyList<ExternalLinkDescriptor> GetLinkDescriptors() =>
        [
            new(
                GetUpdatesLinkId,
                TelegramGetUpdatesExternalLinkTransport.TransportTypeName,
                "telegram://get-updates")
        ];

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

        await SendGetUpdatesAsync(
            offset: null,
            pollTimeoutSeconds: 0,
            perCallTimeoutMs: 5_000,
            bootstrap: true);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandlePollDue(TelegramWaitReplyPollDueEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!IsActiveContinuation(evt.CommandId, evt.Generation))
            return;

        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= State.DeadlineUnixMs)
        {
            await CompleteTimeoutAsync();
            return;
        }

        var currentPollSeconds = ResolveCurrentPollSeconds();
        long? requestedOffset = State.HasNextOffset ? State.NextOffset : null;
        await SendGetUpdatesAsync(
            requestedOffset,
            currentPollSeconds,
            perCallTimeoutMs: (currentPollSeconds + 3) * 1_000,
            bootstrap: false);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleExternalLinkMessageReceived(ExternalLinkMessageReceivedEvent evt)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
        //   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
        ArgumentNullException.ThrowIfNull(evt);
        if (!string.Equals(evt.LinkId, GetUpdatesLinkId, StringComparison.Ordinal))
            return;

        TelegramGetUpdatesResult result;
        try
        {
            result = evt.Payload != null && evt.Payload.Is(TelegramGetUpdatesResult.Descriptor)
                ? evt.Payload.Unpack<TelegramGetUpdatesResult>()
                : TelegramGetUpdatesResult.Parser.ParseFrom(evt.RawPayload);
        }
        catch (Exception ex)
        {
            await CompleteFailureAsync($"telegram getUpdates result parse failed: {ex.Message}");
            return;
        }

        await HandleGetUpdatesResultAsync(result);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTimeoutDue(TelegramWaitReplyTimeoutDueEvent evt)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: watchdog timeout raced outside the actor-owned getUpdates request identity.
        //   New principle: timeout is a self-continuation and stale RequestId callbacks cannot complete state.
        ArgumentNullException.ThrowIfNull(evt);
        if (!IsActiveContinuation(evt.CommandId, evt.Generation))
            return;
        if (!string.IsNullOrWhiteSpace(evt.RequestId) &&
            (State.PendingGetUpdates == null ||
             !string.Equals(State.PendingGetUpdates.RequestId, evt.RequestId, StringComparison.Ordinal)))
        {
            return;
        }

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
                    next.PendingGetUpdates = null;
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

    private async Task SendGetUpdatesAsync(
        long? offset,
        int pollTimeoutSeconds,
        int perCallTimeoutMs,
        bool bootstrap)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: actor turn awaited Telegram /getUpdates connector work behind a Task.Delay watchdog.
        //   New principle: persist pending request, schedule durable timeout, then hand off to ExternalLink stream.
        if (State.PendingGetUpdates != null)
            return;

        var request = BuildGetUpdatesRequest(offset, pollTimeoutSeconds, perCallTimeoutMs, bootstrap);
        var next = State.Clone();
        next.PendingGetUpdates = request.Clone();
        await PersistDomainEventAsync(new TelegramWaitReplyProgressedEvent { State = next });
        await SchedulePendingGetUpdatesTimeoutAsync(request, perCallTimeoutMs);

        if (ExternalLinkPort == null)
        {
            await CompleteFailureAsync("telegram getUpdates external link is not active");
            return;
        }

        try
        {
            await ExternalLinkPort.SendAsync(GetUpdatesLinkId, request);
        }
        catch (Exception ex)
        {
            await CompleteFailureAsync($"telegram getUpdates dispatch failed: {ex.Message}");
        }
    }

    private async Task SchedulePendingGetUpdatesTimeoutAsync(
        TelegramGetUpdatesRequest request,
        int perCallTimeoutMs)
    {
        var dueMs = Math.Max(1, perCallTimeoutMs);
        await ScheduleSelfDurableTimeoutAsync(
            BuildGetUpdatesTimeoutCallbackId(request),
            TimeSpan.FromMilliseconds(dueMs),
            new TelegramWaitReplyTimeoutDueEvent
            {
                CommandId = request.CommandId,
                Generation = request.Generation,
                TimeoutMs = dueMs,
                RequestId = request.RequestId,
            });
    }

    private TelegramGetUpdatesRequest BuildGetUpdatesRequest(
        long? offset,
        int pollTimeoutSeconds,
        int perCallTimeoutMs,
        bool bootstrap)
    {
        var request = new TelegramGetUpdatesRequest
        {
            CommandId = State.CommandId,
            Generation = State.Generation,
            RequestId = Guid.NewGuid().ToString("N"),
            ConnectorName = State.ConnectorName,
            RunId = State.CommandId,
            StepId = State.SessionId,
            PollTimeoutSeconds = Math.Clamp(pollTimeoutSeconds, 0, MaxPollTimeoutSeconds),
            PerCallTimeoutMs = perCallTimeoutMs,
            HttpMethod = "POST",
            ContentType = "application/json",
            Bootstrap = bootstrap,
        };
        request.AllowedUpdates.Add("message");
        request.AllowedUpdates.Add("channel_post");
        request.Parameters.Add(State.ConnectorParameters);
        if (offset.HasValue)
            request.RequestedOffset = offset.Value;
        return request;
    }

    private async Task HandleGetUpdatesResultAsync(TelegramGetUpdatesResult result)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: connector completion raced in-turn watchdog callbacks with no request identity guard.
        //   New principle: only the actor-owned pending RequestId may advance the getUpdates continuation.
        if (!IsActiveContinuation(result.CommandId, result.Generation))
            return;

        if (State.PendingGetUpdates == null ||
            string.IsNullOrWhiteSpace(State.PendingGetUpdates.RequestId) ||
            !string.Equals(State.PendingGetUpdates.RequestId, result.RequestId, StringComparison.Ordinal))
        {
            return;
        }

        if (!result.Success)
        {
            var fallback = result.Bootstrap
                ? "telegram bootstrap getUpdates failed"
                : "telegram getUpdates failed";
            await CompleteFailureAsync(string.IsNullOrWhiteSpace(result.Error) ? fallback : result.Error.Trim());
            return;
        }

        if (result.Bootstrap)
        {
            await HandleBootstrapGetUpdatesResultAsync(result);
            return;
        }

        await HandlePollGetUpdatesResultAsync(result);
    }

    private async Task HandleBootstrapGetUpdatesResultAsync(TelegramGetUpdatesResult result)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: bootstrap /getUpdates ran inline before the actor turn could yield.
        //   New principle: bootstrap is a normal ExternalLink result continuation that updates actor state.
        if (!TryParseTelegramUpdates(
                result.Output,
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
        next.PendingGetUpdates = null;
        if (bootstrapMaxUpdateId.HasValue)
            next.NextOffset = bootstrapMaxUpdateId.Value + 1;
        ApplyMatches(next, matchedUpdates);

        var resolved = ResolveCurrentResult(next, emptyPoll: matchedUpdates.Count == 0);
        if (resolved.HasValue)
        {
            await CompleteResultAsync(resolved.Value);
            return;
        }

        await PersistAndContinuePollAsync(next);
    }

    private async Task HandlePollGetUpdatesResultAsync(TelegramGetUpdatesResult result)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: long-poll results returned directly to the blocked actor turn.
        //   New principle: poll results enter as event continuations and either complete or schedule the next turn.
        if (!TryParseTelegramUpdates(result.Output, out var updates, out var maxUpdateId, out var parseError))
        {
            await CompleteFailureAsync($"telegram getUpdates parse failed: {parseError}");
            return;
        }

        var requestedOffset = result.HasRequestedOffset ? result.RequestedOffset : (long?)null;
        var matchedUpdates = SelectMatchedUpdates(
            updates,
            minimumUpdateId: requestedOffset,
            minimumDateUnixExclusive: null);
        var next = State.Clone();
        next.PendingGetUpdates = null;
        if (maxUpdateId.HasValue)
            next.NextOffset = maxUpdateId.Value + 1;
        ApplyMatches(next, matchedUpdates);

        var resolved = ResolveCurrentResult(next, emptyPoll: matchedUpdates.Count == 0);
        if (resolved.HasValue)
        {
            await CompleteResultAsync(resolved.Value);
            return;
        }

        await PersistAndContinuePollAsync(next);
    }

    private async Task CompleteTimeoutAsync()
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: Task.Delay watchdog raced connector completion outside actor state.
        //   New principle: timeout is an actor event reconciled against PendingGetUpdates or matched reply state.
        if (State.PendingGetUpdates != null)
        {
            await CompleteFailureAsync(
                $"telegram getUpdates timeout after {ResolvePendingGetUpdatesTimeoutMs(State.PendingGetUpdates)}ms");
            return;
        }

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

    private static string BuildGetUpdatesTimeoutCallbackId(TelegramGetUpdatesRequest request) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            GetUpdatesTimeoutCallbackPrefix,
            request.CommandId,
            request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.RequestId);

    private static int ResolvePendingGetUpdatesTimeoutMs(TelegramGetUpdatesRequest request)
    {
        return request.PerCallTimeoutMs > 0 ? request.PerCallTimeoutMs : 1;
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
