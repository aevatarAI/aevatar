using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowRunInsightGAgent
    : GAgentBase<WorkflowRunInsightState>
{
    public const string ActorIdPrefix = "workflow.run.insight";

    public static string BuildActorId(string rootActorId)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            throw new ArgumentException("Root actor id is required.", nameof(rootActorId));

        return $"{ActorIdPrefix}:{rootActorId.Trim()}";
    }

    [EventHandler]
    public Task HandleObservedAsync(WorkflowRunInsightObservedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleTopologyCapturedAsync(WorkflowRunInsightTopologyCapturedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleStoppedAsync(WorkflowRunInsightStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    protected override WorkflowRunInsightState TransitionState(
        WorkflowRunInsightState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunInsightObservedEvent>(ApplyObserved)
            .On<WorkflowRunInsightTopologyCapturedEvent>(ApplyTopologyCaptured)
            .On<WorkflowRunInsightStoppedEvent>(ApplyStopped)
            .OrCurrent();

    private static WorkflowRunInsightState ApplyObserved(
        WorkflowRunInsightState current,
        WorkflowRunInsightObservedEvent evt)
    {
        var next = current.Clone();
        var observedAt = evt.ObservedAtUtc.ToDateTimeOffset();
        var sourceActorId = evt.SourcePublisherActorId ?? string.Empty;
        var payload = evt.ObservedPayload;
        var eventType = evt.ObservedType ?? string.Empty;

        WorkflowRunInsightStateMutations.EnsureInitialized(
            next,
            evt.RootActorId,
            evt.WorkflowName,
            evt.CommandId,
            observedAt);

        if (payload != null && payload.Is(StartWorkflowEvent.Descriptor))
        {
            ApplyStartWorkflow(next, payload.Unpack<StartWorkflowEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(StepRequestEvent.Descriptor))
        {
            ApplyStepRequest(next, payload.Unpack<StepRequestEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(StepCompletedEvent.Descriptor))
        {
            ApplyStepCompleted(next, payload.Unpack<StepCompletedEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(WorkflowSuspendedEvent.Descriptor))
        {
            ApplyWorkflowSuspended(next, payload.Unpack<WorkflowSuspendedEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(WorkflowStoppedEvent.Descriptor))
        {
            ApplyWorkflowStopped(next, payload.Unpack<WorkflowStoppedEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            ApplyWorkflowCompleted(next, payload.Unpack<WorkflowCompletedEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(WaitingForSignalEvent.Descriptor))
        {
            ApplyWaitingForSignal(next, payload.Unpack<WaitingForSignalEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(WorkflowSignalBufferedEvent.Descriptor))
        {
            ApplySignalBuffered(next, payload.Unpack<WorkflowSignalBufferedEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.TextMessageStartEvent.Descriptor))
        {
            ApplyTextMessageStart(next, payload.Unpack<Aevatar.AI.Abstractions.TextMessageStartEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.TextMessageContentEvent.Descriptor))
        {
            ApplyTextMessageContent(next, payload.Unpack<Aevatar.AI.Abstractions.TextMessageContentEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.TextMessageEndEvent.Descriptor))
        {
            ApplyTextMessageEnd(next, payload.Unpack<Aevatar.AI.Abstractions.TextMessageEndEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.ChatResponseEvent.Descriptor))
        {
            ApplyChatResponse(next, payload.Unpack<Aevatar.AI.Abstractions.ChatResponseEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.TextMessageReasoningEvent.Descriptor))
        {
            ApplyReasoning(next, payload.Unpack<Aevatar.AI.Abstractions.TextMessageReasoningEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.ToolCallEvent.Descriptor))
        {
            ApplyToolCall(next, payload.Unpack<Aevatar.AI.Abstractions.ToolCallEvent>(), sourceActorId, eventType, observedAt);
        }
        else if (payload != null && payload.Is(Aevatar.AI.Abstractions.ToolResultEvent.Descriptor))
        {
            ApplyToolResult(next, payload.Unpack<Aevatar.AI.Abstractions.ToolResultEvent>(), sourceActorId, eventType, observedAt);
        }

        WorkflowRunInsightStateMutations.RecordObserved(
            next,
            evt.SourceEventId,
            evt.StateVersion,
            observedAt);
        WorkflowRunInsightStateMutations.RefreshDerivedFields(next, observedAt);
        return next;
    }

    private static WorkflowRunInsightState ApplyTopologyCaptured(
        WorkflowRunInsightState current,
        WorkflowRunInsightTopologyCapturedEvent evt)
    {
        var next = current.Clone();
        var capturedAt = evt.CapturedAtUtc.ToDateTimeOffset();

        WorkflowRunInsightStateMutations.EnsureInitialized(
            next,
            evt.RootActorId,
            evt.WorkflowName,
            evt.CommandId,
            capturedAt);
        WorkflowRunInsightStateMutations.ReplaceTopology(next, evt.TopologyEntries);

        if (next.EndedAt < next.StartedAt)
            next.EndedAt = capturedAt;
        if (next.CompletionStatus == WorkflowRunInsightCompletionStatus.Running)
            next.CompletionStatus = WorkflowRunInsightCompletionStatus.Completed;

        WorkflowRunInsightStateMutations.RefreshDerivedFields(next, capturedAt);
        return next;
    }

    private static WorkflowRunInsightState ApplyStopped(
        WorkflowRunInsightState current,
        WorkflowRunInsightStoppedEvent evt)
    {
        var next = current.Clone();
        var stoppedAt = evt.StoppedAtUtc.ToDateTimeOffset();

        WorkflowRunInsightStateMutations.EnsureInitialized(
            next,
            evt.RootActorId,
            next.WorkflowName,
            next.CommandId,
            stoppedAt);

        if (next.CompletionStatus is WorkflowRunInsightCompletionStatus.Running or WorkflowRunInsightCompletionStatus.Unknown)
            next.CompletionStatus = WorkflowRunInsightCompletionStatus.Stopped;
        if (next.EndedAt < next.StartedAt)
            next.EndedAt = stoppedAt;
        if (!string.IsNullOrWhiteSpace(evt.Reason) && string.IsNullOrWhiteSpace(next.FinalError))
            next.FinalError = evt.Reason;

        WorkflowRunInsightStateMutations.RefreshDerivedFields(next, stoppedAt);
        return next;
    }

    private static void ApplyStartWorkflow(
        WorkflowRunInsightState state,
        StartWorkflowEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        if (!string.IsNullOrWhiteSpace(evt.WorkflowName))
            state.WorkflowName = evt.WorkflowName;
        state.Input = evt.Input ?? string.Empty;
        state.StartedAt = observedAt;
        state.EndedAt = observedAt;
        state.CompletionStatus = WorkflowRunInsightCompletionStatus.Running;

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        AppendIfPresent(data, evt.Parameters, "session_id");
        AppendIfPresent(data, evt.Parameters, "channel_id");
        AppendIfPresent(data, evt.Parameters, "scope_id");
        AppendIfPresent(data, evt.Parameters, "message_id");
        AppendIfPresent(data, evt.Parameters, "correlation_id");
        AppendIfPresent(data, evt.Parameters, "idempotency_key");

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.start",
            $"command={state.CommandId}",
            sourceActorId,
            null,
            null,
            eventType,
            data);
    }

    private static void ApplyStepRequest(
        WorkflowRunInsightState state,
        StepRequestEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var step = WorkflowRunInsightStateMutations.GetOrCreateStep(state, evt.StepId);
        step.StepType = evt.StepType ?? string.Empty;
        step.TargetRole = evt.TargetRole ?? string.Empty;
        step.RequestedAt = observedAt;
        WorkflowRunInsightStateMutations.ReplaceMap(step.RequestParametersMap, evt.Parameters);

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "step.request",
            $"{evt.StepId} ({evt.StepType})",
            sourceActorId,
            evt.StepId,
            evt.StepType,
            eventType,
            evt.Parameters);
    }

    private static void ApplyStepCompleted(
        WorkflowRunInsightState state,
        StepCompletedEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var step = WorkflowRunInsightStateMutations.GetOrCreateStep(state, evt.StepId);
        step.CompletedAt = observedAt;
        step.Success = evt.Success;
        step.Error = evt.Error ?? string.Empty;
        step.WorkerId = evt.WorkerId ?? string.Empty;
        step.OutputPreview = WorkflowRunInsightStateMutations.Truncate(evt.Output ?? string.Empty, 240);
        WorkflowRunInsightStateMutations.ReplaceMap(step.CompletionAnnotationsMap, evt.Annotations);
        step.NextStepId = evt.NextStepId ?? string.Empty;
        step.BranchKey = evt.BranchKey ?? string.Empty;
        step.AssignedVariable = evt.AssignedVariable ?? string.Empty;
        step.AssignedValue = evt.AssignedValue ?? string.Empty;

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "step.completed",
            $"{evt.StepId} success={evt.Success}",
            sourceActorId,
            evt.StepId,
            step.StepType,
            eventType,
            evt.Annotations);
    }

    private static void ApplyWorkflowSuspended(
        WorkflowRunInsightState state,
        WorkflowSuspendedEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var step = WorkflowRunInsightStateMutations.GetOrCreateStep(state, evt.StepId);
        step.SuspensionType = evt.SuspensionType ?? string.Empty;
        step.SuspensionPrompt = evt.Prompt ?? string.Empty;
        step.SuspensionTimeoutSecondsValue = evt.TimeoutSeconds;
        step.RequestedVariableName = evt.VariableName ?? string.Empty;
        state.CompletionStatus = WorkflowRunInsightCompletionStatus.WaitingForSignal;

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["suspension_type"] = evt.SuspensionType ?? string.Empty,
            ["prompt"] = evt.Prompt ?? string.Empty,
            ["timeout_seconds"] = evt.TimeoutSeconds.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(evt.VariableName))
            data["variable_name"] = evt.VariableName;

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.suspended",
            $"Workflow suspended at step {evt.StepId}: {evt.SuspensionType}",
            sourceActorId,
            evt.StepId,
            evt.SuspensionType,
            eventType,
            data);
    }

    private static void ApplyWorkflowCompleted(
        WorkflowRunInsightState state,
        WorkflowCompletedEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        state.Success = evt.Success;
        state.FinalOutput = evt.Output ?? string.Empty;
        state.FinalError = evt.Error ?? string.Empty;
        state.EndedAt = observedAt;
        state.CompletionStatus = evt.Success
            ? WorkflowRunInsightCompletionStatus.Completed
            : WorkflowRunInsightCompletionStatus.Failed;

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.completed",
            $"success={evt.Success}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["command_id"] = state.CommandId,
            });
    }

    private static void ApplyWorkflowStopped(
        WorkflowRunInsightState state,
        WorkflowStoppedEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        state.Success = null;
        state.FinalOutput = string.Empty;
        state.FinalError = evt.Reason ?? string.Empty;
        state.EndedAt = observedAt;
        state.CompletionStatus = WorkflowRunInsightCompletionStatus.Stopped;

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.stopped",
            string.IsNullOrWhiteSpace(evt.Reason) ? "workflow stopped" : evt.Reason,
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["command_id"] = state.CommandId,
                ["reason"] = evt.Reason ?? string.Empty,
            });
    }

    private static void ApplyWaitingForSignal(
        WorkflowRunInsightState state,
        WaitingForSignalEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        state.CompletionStatus = WorkflowRunInsightCompletionStatus.WaitingForSignal;
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.waiting_signal",
            $"step={evt.StepId}, signal={evt.SignalName}",
            sourceActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
                ["prompt"] = evt.Prompt ?? string.Empty,
                ["timeout_ms"] = evt.TimeoutMs.ToString(),
            });
    }

    private static void ApplySignalBuffered(
        WorkflowRunInsightState state,
        WorkflowSignalBufferedEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "workflow.signal_buffered",
            $"step={evt.StepId}, signal={evt.SignalName}",
            sourceActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
                ["payload"] = evt.Payload ?? string.Empty,
            });
    }

    private static void ApplyTextMessageStart(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.TextMessageStartEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "llm.start",
            $"agent={sourceActorId}, session={evt.SessionId ?? string.Empty}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            });
    }

    private static void ApplyTextMessageContent(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.TextMessageContentEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var delta = evt.Delta ?? string.Empty;
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "llm.content",
            $"agent={sourceActorId}, chars={delta.Length}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
                ["delta_length"] = delta.Length.ToString(),
            });
    }

    private static void ApplyTextMessageEnd(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.TextMessageEndEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var content = evt.Content ?? string.Empty;
        if (!string.Equals(sourceActorId, state.RootActorId, StringComparison.Ordinal))
        {
            WorkflowRunInsightStateMutations.AddRoleReply(
                state,
                observedAt,
                sourceActorId,
                evt.SessionId ?? string.Empty,
                content);
        }

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "llm.end",
            $"agent={sourceActorId}, chars={content.Length}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            });
    }

    private static void ApplyChatResponse(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.ChatResponseEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        var content = evt.Content ?? string.Empty;
        if (!string.Equals(sourceActorId, state.RootActorId, StringComparison.Ordinal))
        {
            WorkflowRunInsightStateMutations.AddRoleReply(
                state,
                observedAt,
                sourceActorId,
                evt.SessionId ?? string.Empty,
                content);
        }

        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "llm.response",
            $"agent={sourceActorId}, chars={content.Length}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            });
    }

    private static void ApplyReasoning(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.TextMessageReasoningEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "llm.reasoning",
            $"agent={sourceActorId}, chars={(evt.Delta ?? string.Empty).Length}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
                ["delta"] = evt.Delta ?? string.Empty,
            });
    }

    private static void ApplyToolCall(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.ToolCallEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "tool.call",
            $"agent={sourceActorId}, tool={evt.ToolName ?? string.Empty}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool_name"] = evt.ToolName ?? string.Empty,
                ["call_id"] = evt.CallId ?? string.Empty,
            });
    }

    private static void ApplyToolResult(
        WorkflowRunInsightState state,
        Aevatar.AI.Abstractions.ToolResultEvent evt,
        string sourceActorId,
        string eventType,
        DateTimeOffset observedAt)
    {
        WorkflowRunInsightStateMutations.AddTimeline(
            state,
            observedAt,
            "tool.result",
            $"agent={sourceActorId}, call={evt.CallId ?? string.Empty}, success={evt.Success}",
            sourceActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["call_id"] = evt.CallId ?? string.Empty,
                ["success"] = evt.Success.ToString(),
                ["error"] = evt.Error ?? string.Empty,
            });
    }

    private static void AppendIfPresent(
        IDictionary<string, string> target,
        Google.Protobuf.Collections.MapField<string, string> source,
        string sourceKey,
        string? targetKey = null)
    {
        if (!source.TryGetValue(sourceKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return;

        target[targetKey ?? sourceKey] = raw.Trim();
    }
}
