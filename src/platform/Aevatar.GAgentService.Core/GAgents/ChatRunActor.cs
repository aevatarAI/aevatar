using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ChatRunActor : GAgentBase<ChatRunState>
{
    private static readonly Duration DefaultIdleTtl = Duration.FromTimeSpan(TimeSpan.FromMinutes(30));

    // Refactor (issue1334): Old pattern: ChatRun persisted folded tool results used generic ResultJson naming. New principle: ChatRun actor state and events quarantine that JSON as internal_result_json while boundary payload names remain explicit.
    public ChatRunActor()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartAsync(StartChatRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!string.IsNullOrWhiteSpace(State.ResponseId))
            return;

        var now = command.StartedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        await PersistDomainEventAsync(new ChatRunStartedEvent
        {
            ResponseId = NormalizeRequired(command.ResponseId, nameof(command.ResponseId)),
            ModelName = command.ModelName?.Trim() ?? string.Empty,
            Messages = { command.Messages.Select(static message => message.Clone()) },
            IdleTtl = command.IdleTtl ?? DefaultIdleTtl,
            StartedAt = now,
        });
    }

    [EventHandler]
    public async Task HandleRecordAssistantDraftAsync(RecordChatRunAssistantDraftRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActive();

        await PersistDomainEventAsync(new ChatRunAssistantDraftRecordedEvent
        {
            Content = command.Content ?? string.Empty,
            LlmRound = command.LlmRound,
            ObservedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandlePrepareSubRunObservationAsync(PrepareChatRunSubRunObservationRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActive();

        if (command.WaitMode != ChatRunSubRunWaitMode.Complete)
            return;

        var runId = NormalizeRequired(command.RunId, nameof(command.RunId));
        var targetActorId = FirstNonEmpty(command.ActorId, command.TargetId);
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new InvalidOperationException("actor_id is required to observe a complete sub-run.");

        await PersistDomainEventAsync(new ChatRunSubRunObservationStartedEvent
        {
            RunId = runId,
            ToolCallId = NormalizeRequired(command.ToolCallId, nameof(command.ToolCallId)),
            ToolName = NormalizeRequired(command.ToolName, nameof(command.ToolName)),
            Arguments = command.Arguments?.Clone() ?? new Struct(),
            TargetKind = command.TargetKind,
            TargetId = command.TargetId ?? string.Empty,
            WaitMode = command.WaitMode,
            StreamTopic = command.StreamTopic ?? string.Empty,
            ActorId = targetActorId,
            ServiceId = command.ServiceId ?? string.Empty,
            EndpointId = command.EndpointId ?? string.Empty,
            LlmRound = command.LlmRound,
            ObservedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });

        await EnsureObservationRelayAsync(targetActorId, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubmitToolCallAsync(SubmitChatRunToolCallRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActive();

        var observedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        await PersistDomainEventAsync(new ChatRunToolCallSubmittedEvent
        {
            ToolCallId = NormalizeRequired(command.ToolCallId, nameof(command.ToolCallId)),
            ToolName = NormalizeRequired(command.ToolName, nameof(command.ToolName)),
            Arguments = command.Arguments?.Clone() ?? new Struct(),
            InternalResultJson = command.InternalResultJson ?? string.Empty,
            RunId = command.RunId ?? string.Empty,
            TargetKind = command.TargetKind,
            TargetId = command.TargetId ?? string.Empty,
            WaitMode = command.WaitMode,
            StreamTopic = command.StreamTopic ?? string.Empty,
            ActorId = command.ActorId ?? string.Empty,
            ServiceId = command.ServiceId ?? string.Empty,
            EndpointId = command.EndpointId ?? string.Empty,
            LlmRound = command.LlmRound,
            ObservedAt = observedAt,
        });
    }

    [EventHandler]
    public async Task HandleSubRunTerminalAsync(ChatRunSubRunTerminalObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        EnsureActive();

        var runId = NormalizeRequired(observed.RunId, nameof(observed.RunId));
        var pending = State.ActiveSubRunSubscriptions
            .FirstOrDefault(subscription => string.Equals(subscription.RunId, runId, StringComparison.Ordinal));
        if (pending == null)
            return;

        var internalResultJson = string.IsNullOrWhiteSpace(observed.InternalResultJson)
            ? BuildDefaultResultJson(pending, observed)
            : observed.InternalResultJson;
        await PersistDomainEventAsync(new ChatRunSubRunTerminalFoldedEvent
        {
            RunId = runId,
            CallerToolCallId = pending.CallerToolCallId,
            ToolName = pending.ToolName,
            InternalResultJson = internalResultJson,
            LlmRound = pending.LlmRound,
            Status = observed.Status ?? string.Empty,
            ActorId = FirstNonEmpty(observed.ActorId, pending.ActorId),
            ServiceId = FirstNonEmpty(observed.ServiceId, pending.ServiceId),
            EndpointId = FirstNonEmpty(observed.EndpointId, pending.EndpointId),
            CompletionObserved = observed.CompletionObserved,
            ObservedAt = observed.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });

        // Refactor (iter290/cluster001): Old pattern: ready notifications required consumers to parse ResultJson for completion facts. New principle: ready notifications publish typed completion facts with the result payload.
        await PublishAsync(new ChatRunToolResultReady
        {
            ResponseId = State.ResponseId,
            RunId = runId,
            CallerToolCallId = pending.CallerToolCallId,
            ToolName = pending.ToolName,
            InternalResultJson = internalResultJson,
            LlmRound = pending.LlmRound,
            Status = observed.Status ?? string.Empty,
            ActorId = FirstNonEmpty(observed.ActorId, pending.ActorId),
            ServiceId = FirstNonEmpty(observed.ServiceId, pending.ServiceId),
            EndpointId = FirstNonEmpty(observed.EndpointId, pending.EndpointId),
            CompletionObserved = observed.CompletionObserved,
        }, TopologyAudience.Self);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleObservedEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (State.Terminated ||
            !envelope.Route.IsObserverPublication() ||
            !StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) ||
            StreamForwardingRules.IsTransitOnlyForwarding(envelope) ||
            envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
        {
            return;
        }

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateEvent?.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
            return;

        var completed = published.StateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>();
        if (string.IsNullOrWhiteSpace(completed.SessionId))
            return;

        var pending = State.ActiveSubRunSubscriptions.FirstOrDefault(subscription =>
            string.Equals(subscription.RunId, completed.SessionId, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(subscription.ActorId) ||
             string.Equals(subscription.ActorId, envelope.Route.PublisherActorId, StringComparison.Ordinal)));
        if (pending == null)
            return;

        await HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = pending.RunId,
            Status = ResolveGAgentTerminalStatus(completed),
            InternalResultJson = BuildGAgentTerminalResultJson(pending, completed),
            ActorId = pending.ActorId,
            ServiceId = pending.ServiceId,
            EndpointId = pending.EndpointId,
            CompletionObserved = true,
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleTerminateAsync(TerminateChatRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (State.Terminated)
            return;

        var activeObservationTargets = State.ActiveSubRunSubscriptions
            .Select(static subscription => FirstNonEmpty(subscription.ActorId, subscription.TargetId))
            .Where(static targetActorId => !string.IsNullOrWhiteSpace(targetActorId))
            .ToArray();

        await PersistDomainEventAsync(new ChatRunTerminatedEvent
        {
            Reason = command.Reason ?? string.Empty,
            ObservedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });

        foreach (var targetActorId in activeObservationTargets)
            await RemoveObservationRelayBestEffortAsync(targetActorId, CancellationToken.None);
    }

    protected override ChatRunState TransitionState(ChatRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChatRunStartedEvent>(ApplyStarted)
            .On<ChatRunAssistantDraftRecordedEvent>(ApplyAssistantDraftRecorded)
            .On<ChatRunToolCallSubmittedEvent>(ApplyToolCallSubmitted)
            .On<ChatRunSubRunObservationStartedEvent>(ApplySubRunObservationStarted)
            .On<ChatRunSubRunTerminalFoldedEvent>(ApplySubRunTerminalFolded)
            .On<ChatRunTerminatedEvent>(ApplyTerminated)
            .OrCurrent();

    private static ChatRunState ApplyStarted(ChatRunState current, ChatRunStartedEvent evt)
    {
        var next = current.Clone();
        next.ResponseId = evt.ResponseId;
        next.ModelName = evt.ModelName;
        next.Messages.Clear();
        next.Messages.Add(evt.Messages.Select(static message => message.Clone()));
        next.IdleTtl = evt.IdleTtl ?? DefaultIdleTtl;
        next.InitializedAt = evt.StartedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastActivityAt = next.InitializedAt.Clone();
        return next;
    }

    private static ChatRunState ApplyAssistantDraftRecorded(
        ChatRunState current,
        ChatRunAssistantDraftRecordedEvent evt)
    {
        var next = current.Clone();
        next.AssistantContentDraft = evt.Content;
        next.CurrentLlmRound = Math.Max(next.CurrentLlmRound, evt.LlmRound);
        next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static ChatRunState ApplyToolCallSubmitted(ChatRunState current, ChatRunToolCallSubmittedEvent evt)
    {
        var next = current.Clone();
        var existingHistory = next.ToolCallHistory
            .LastOrDefault(call => string.Equals(call.ToolCallId, evt.ToolCallId, StringComparison.Ordinal));
        var alreadyFolded = existingHistory != null && !string.IsNullOrWhiteSpace(existingHistory.InternalResultJson);
        if (existingHistory == null)
        {
            next.ToolCallHistory.Add(new ChatRunToolCallRecord
            {
                ToolCallId = evt.ToolCallId,
                ToolName = evt.ToolName,
                Arguments = evt.Arguments?.Clone() ?? new Struct(),
                InternalResultJson = evt.InternalResultJson,
                LlmRound = evt.LlmRound,
                RunId = evt.RunId,
                ObservedAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        if (!alreadyFolded &&
            evt.WaitMode == ChatRunSubRunWaitMode.Complete &&
            !string.IsNullOrWhiteSpace(evt.RunId))
        {
            UpsertActiveSubRunSubscription(
                next,
                evt.RunId,
                evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
                evt.TargetKind,
                evt.TargetId,
                evt.WaitMode,
                evt.StreamTopic,
                evt.ToolCallId,
                evt.ToolName,
                evt.ActorId,
                evt.ServiceId,
                evt.EndpointId,
                evt.LlmRound);
        }

        next.CurrentLlmRound = Math.Max(next.CurrentLlmRound, evt.LlmRound);
        next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static ChatRunState ApplySubRunObservationStarted(
        ChatRunState current,
        ChatRunSubRunObservationStartedEvent evt)
    {
        var next = current.Clone();
        var existingHistory = next.ToolCallHistory
            .LastOrDefault(call => string.Equals(call.ToolCallId, evt.ToolCallId, StringComparison.Ordinal));
        if (existingHistory != null && !string.IsNullOrWhiteSpace(existingHistory.InternalResultJson))
        {
            next.CurrentLlmRound = Math.Max(next.CurrentLlmRound, evt.LlmRound);
            next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
            return next;
        }

        if (existingHistory == null)
        {
            next.ToolCallHistory.Add(new ChatRunToolCallRecord
            {
                ToolCallId = evt.ToolCallId,
                ToolName = evt.ToolName,
                Arguments = evt.Arguments?.Clone() ?? new Struct(),
                InternalResultJson = string.Empty,
                LlmRound = evt.LlmRound,
                RunId = evt.RunId,
                ObservedAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        UpsertActiveSubRunSubscription(
            next,
            evt.RunId,
            evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
            evt.TargetKind,
            evt.TargetId,
            evt.WaitMode,
            evt.StreamTopic,
            evt.ToolCallId,
            evt.ToolName,
            evt.ActorId,
            evt.ServiceId,
            evt.EndpointId,
            evt.LlmRound);
        next.CurrentLlmRound = Math.Max(next.CurrentLlmRound, evt.LlmRound);
        next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static ChatRunState ApplySubRunTerminalFolded(
        ChatRunState current,
        ChatRunSubRunTerminalFoldedEvent evt)
    {
        var next = current.Clone();
        var index = FindSubscriptionIndex(next, evt.RunId);
        if (index >= 0)
            next.ActiveSubRunSubscriptions.RemoveAt(index);

        var history = next.ToolCallHistory
            .LastOrDefault(call => string.Equals(call.RunId, evt.RunId, StringComparison.Ordinal));
        if (history != null)
            history.InternalResultJson = evt.InternalResultJson;

        next.Messages.Add(new ChatRunMessageRecord
        {
            Role = "tool",
            ToolCallId = evt.CallerToolCallId,
            Content = evt.InternalResultJson,
        });
        next.CurrentLlmRound = evt.LlmRound + 1;
        next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static ChatRunState ApplyTerminated(ChatRunState current, ChatRunTerminatedEvent evt)
    {
        var next = current.Clone();
        next.Terminated = true;
        next.ActiveSubRunSubscriptions.Clear();
        next.LastActivityAt = evt.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private void EnsureActive()
    {
        if (string.IsNullOrWhiteSpace(State.ResponseId))
            throw new InvalidOperationException($"ChatRunActor '{Id}' has not been started.");
        if (State.Terminated)
            throw new InvalidOperationException($"ChatRunActor '{Id}' is terminated.");
    }

    private static int FindSubscriptionIndex(ChatRunState state, string runId)
    {
        for (var i = 0; i < state.ActiveSubRunSubscriptions.Count; i++)
        {
            if (string.Equals(state.ActiveSubRunSubscriptions[i].RunId, runId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private Task EnsureObservationRelayAsync(string? targetActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(targetActorId)
            .UpsertRelayAsync(BuildCommittedEventObservationRelayBinding(targetActorId), ct);
    }

    private async Task RemoveObservationRelayBestEffortAsync(string? targetActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            return;

        try
        {
            await Services
                .GetRequiredService<IStreamProvider>()
                .GetStream(targetActorId)
                .RemoveRelayAsync(Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Failed to remove chat run sub-run observation relay. actorId={ActorId} targetActorId={TargetActorId}",
                Id,
                targetActorId);
        }
    }

    private StreamForwardingBinding BuildCommittedEventObservationRelayBinding(string targetActorId)
    {
        var typeUrl = $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}";
        return new StreamForwardingBinding
        {
            SourceStreamId = targetActorId,
            TargetStreamId = Id,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                typeUrl,
            },
        };
    }

    private static void UpsertActiveSubRunSubscription(
        ChatRunState state,
        string runId,
        Timestamp startedAt,
        ChatRunSubRunTargetKind targetKind,
        string targetId,
        ChatRunSubRunWaitMode waitMode,
        string streamTopic,
        string callerToolCallId,
        string toolName,
        string actorId,
        string serviceId,
        string endpointId,
        int llmRound)
    {
        var subscription = new ChatRunSubRunSubscription
        {
            RunId = runId,
            StartedAt = startedAt,
            TargetKind = targetKind,
            TargetId = targetId,
            WaitMode = waitMode,
            StreamTopic = streamTopic,
            CallerToolCallId = callerToolCallId,
            ToolName = toolName,
            ActorId = actorId,
            ServiceId = serviceId,
            EndpointId = endpointId,
            LlmRound = llmRound,
        };

        var existingIndex = FindSubscriptionIndex(state, runId);
        if (existingIndex >= 0)
            state.ActiveSubRunSubscriptions[existingIndex] = subscription;
        else
            state.ActiveSubRunSubscriptions.Add(subscription);
    }

    private static string BuildDefaultResultJson(
        ChatRunSubRunSubscription pending,
        ChatRunSubRunTerminalObserved observed)
    {
        var status = string.IsNullOrWhiteSpace(observed.Status) ? "completed" : observed.Status;
        var actorId = FirstNonEmpty(observed.ActorId, pending.ActorId);
        var serviceId = FirstNonEmpty(observed.ServiceId, pending.ServiceId);
        var endpointId = FirstNonEmpty(observed.EndpointId, pending.EndpointId);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            run_id = pending.RunId,
            status,
            actor_id = EmptyToNull(actorId),
            service_id = EmptyToNull(serviceId),
            endpoint_id = EmptyToNull(endpointId),
            wait = "complete",
        }, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string BuildGAgentTerminalResultJson(
        ChatRunSubRunSubscription pending,
        RoleChatSessionCompletedEvent completed)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            run_id = pending.RunId,
            status = ResolveGAgentTerminalStatus(completed),
            actor_id = EmptyToNull(pending.ActorId),
            content = EmptyToNull(completed.Content ?? string.Empty),
            reasoning_content = EmptyToNull(completed.ReasoningContent ?? string.Empty),
            content_emitted = completed.ContentEmitted,
            wait = "complete",
        }, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string ResolveGAgentTerminalStatus(RoleChatSessionCompletedEvent completed)
    {
        var content = completed.Content ?? string.Empty;
        return content.StartsWith("[[AEVATAR_LLM_ERROR]]", StringComparison.Ordinal) ||
               content.StartsWith("LLM request failed", StringComparison.Ordinal)
            ? GAgentRunTerminalStatus.Failed.ToString()
            : GAgentRunTerminalStatus.TextMessageCompleted.ToString();
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() :
        !string.IsNullOrWhiteSpace(second) ? second.Trim() : string.Empty;

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
