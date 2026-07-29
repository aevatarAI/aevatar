using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Projectors;

internal static class WorkflowExecutionArtifactMaterializationSupport
{
    private delegate void ObservedPayloadHandler(
        WorkflowRunInsightReportDocument readModel,
        Google.Protobuf.WellKnownTypes.Any payload,
        DateTimeOffset observedAt);

    private static readonly IReadOnlyDictionary<string, ObservedPayloadHandler> ObservedPayloadHandlers =
        new Dictionary<string, ObservedPayloadHandler>(StringComparer.Ordinal)
        {
            [BuildTypeUrl(WorkflowRunExecutionStartedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowRunExecutionStarted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRunExecutionStartedEvent>(),
                    observedAt),
            [BuildTypeUrl(StepRequestEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyStepRequest(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<StepRequestEvent>(),
                    observedAt),
            [BuildTypeUrl(StepCompletedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyStepCompleted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<StepCompletedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowSuspendedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowSuspended(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowSuspendedEvent>(),
                    observedAt),
            [BuildTypeUrl(WaitingForSignalEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWaitingForSignal(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WaitingForSignalEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowSignalBufferedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowSignalBuffered(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowSignalBufferedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowRoleActorLinkedEvent.Descriptor)] = static (readModel, payload, _) =>
                ApplyWorkflowRoleActorLinked(
                    readModel,
                    payload.Unpack<WorkflowRoleActorLinkedEvent>()),
            [BuildTypeUrl(SubWorkflowBindingUpsertedEvent.Descriptor)] = static (readModel, payload, _) =>
                ApplySubWorkflowBindingUpserted(
                    readModel,
                    payload.Unpack<SubWorkflowBindingUpsertedEvent>()),
            [BuildTypeUrl(WorkflowRoleReplyRecordedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowRoleReplyRecorded(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRoleReplyRecordedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowCompletedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowCompleted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowCompletedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowStoppedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowStopped(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowStoppedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowRunStoppedEvent.Descriptor)] = static (readModel, payload, observedAt) =>
                ApplyWorkflowRunStopped(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRunStoppedEvent>(),
                    observedAt),
        };

    public static bool TryUnpackRootStateEnvelope(
        EventEnvelope envelope,
        out StateEvent? stateEvent,
        out WorkflowRunState? state)
    {
        if (CommittedStateEventEnvelope.TryUnpackState<WorkflowRunState>(
                envelope,
                out _,
                out stateEvent,
                out state) &&
            stateEvent != null &&
            state != null)
        {
            return true;
        }

        stateEvent = null;
        state = null;
        return false;
    }

    public static bool ShouldSkip(IProjectionReadModel existing, StateEvent stateEvent)
    {
        if (existing.StateVersion > stateEvent.Version)
            return true;

        return existing.StateVersion == stateEvent.Version &&
               string.Equals(existing.LastEventId, stateEvent.EventId ?? string.Empty, StringComparison.Ordinal);
    }

    public static WorkflowRunInsightReportDocument CreateReportDocument(
        WorkflowExecutionMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        var readModel = new WorkflowRunInsightReportDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            ReportVersion = "3.0",
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            // Refactor (iter33/cluster-035-workflow-report-runtime-topology-sideread):
            //   Old pattern: Workflow report 用 IActorRuntime.GetAsync(...).GetChildrenIdsAsync() 读 runtime children 当 topology 事实,违反 runtime-shape-not-fact + side-read
            //   New principle: 删 IWorkflowExecutionTopologyResolver + ActorRuntimeWorkflowExecutionTopologyResolver;topology 从 committed event projection 来(WorkflowRoleActorLinkedEvent + SubWorkflowBindingUpsertedEvent 已 materialize);enum 值 RuntimeSnapshot 改 CommittedProjection;无 proto 改
            TopologySource = WorkflowExecutionTopologySource.CommittedProjection,
            CreatedAt = observedAt,
        };
        ApplyReportBase(readModel, context, state, stateEvent, observedAt);
        return readModel;
    }

    public static void ApplyReportBase(
        WorkflowRunInsightReportDocument readModel,
        WorkflowExecutionMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        readModel.Id = context.RootActorId;
        readModel.RootActorId = context.RootActorId;
        readModel.CommandId = state.LastCommandId ?? string.Empty;
        readModel.WorkflowName = ResolveWorkflowName(state, readModel.WorkflowName);
        readModel.Input = SanitizeAuditText(state.Input);
        readModel.FinalOutput = SanitizeAuditText(state.FinalOutput);
        readModel.FinalError = SanitizeAuditText(state.FinalError);
        readModel.Success = ResolveSuccess(state.Status);
        readModel.CompletionStatus = ResolveCompletionStatus(state.Status, readModel.CompletionStatus);
        readModel.StateVersion = stateEvent.Version;
        readModel.LastEventId = stateEvent.EventId ?? string.Empty;
        readModel.UpdatedAt = observedAt;

        if (readModel.CreatedAt == default)
            readModel.CreatedAt = observedAt;
        if (readModel.StartedAt == default && string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
            readModel.StartedAt = observedAt;
        if (IsTerminalStatus(state.Status))
            readModel.EndedAt = observedAt;
    }

    public static void ApplyObservedPayloadToReport(
        WorkflowRunInsightReportDocument readModel,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        var payload = stateEvent.EventData;
        if (payload == null)
        {
            RefreshSummary(readModel);
            return;
        }

        if (ObservedPayloadHandlers.TryGetValue(payload.TypeUrl ?? string.Empty, out var handler))
            handler(readModel, payload, observedAt);

        RefreshSummary(readModel);
    }

    private static void ApplyWorkflowRunExecutionStarted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRunExecutionStartedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? readModel.WorkflowName : evt.WorkflowName;
        readModel.Input = SanitizeAuditText(evt.Input);
        if (readModel.StartedAt == default)
            readModel.StartedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.start",
            $"command={readModel.CommandId}",
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static void ApplyStepRequest(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        StepRequestEvent evt,
        DateTimeOffset observedAt)
    {
        var step = GetOrCreateStep(readModel.Steps, evt.StepId);
        step.StepId = evt.StepId ?? string.Empty;
        step.StepType = evt.StepType ?? string.Empty;
        step.TargetRole = evt.TargetRole ?? string.Empty;
        step.RequestedAt = observedAt;
        var parameters = WorkflowStepParameterProjectionSource.From(evt);
        ReplaceMap(step.RequestParameters, parameters);
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "step.request",
            $"{evt.StepId} ({evt.StepType})",
            readModel.RootActorId,
            evt.StepId,
            evt.StepType,
            eventType,
            parameters);
    }

    private static void ApplyStepCompleted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        StepCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        var step = GetOrCreateStep(readModel.Steps, evt.StepId);
        step.StepId = evt.StepId ?? string.Empty;
        step.CompletedAt = observedAt;
        step.Success = evt.Success;
        step.OutputPreview = SanitizeAuditTextForDisplay(evt.Output, 240);
        step.Error = SanitizeAuditText(evt.Error);
        step.WorkerId = evt.WorkerId ?? string.Empty;
        step.NextStepId = evt.NextStepId ?? string.Empty;
        step.BranchKey = evt.BranchKey ?? string.Empty;
        step.AssignedVariable = evt.AssignedVariable ?? string.Empty;
        step.AssignedValue = SanitizeAuditValue(evt.AssignedVariable, evt.AssignedValue);
        step.Usage = ToReadModelUsage(evt.Usage);
        ReplaceMap(step.CompletionAnnotations, evt.Annotations);
        AddTimeline(
            readModel.Timeline,
            observedAt,
            evt.Success ? "step.completed" : "step.failed",
            $"{evt.StepId} ({(evt.Success ? "success" : "failed")})",
            evt.WorkerId,
            evt.StepId,
            step.StepType,
            eventType,
            evt.Annotations);
    }

    private static void ApplyWorkflowSuspended(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowSuspendedEvent evt,
        DateTimeOffset observedAt)
    {
        var step = GetOrCreateStep(readModel.Steps, evt.StepId);
        step.SuspensionType = evt.SuspensionType ?? string.Empty;
        step.SuspensionPrompt = SanitizeAuditText(evt.Prompt);
        step.SuspensionContent = evt.Secure
            ? string.Empty
            : SanitizeAuditText(evt.Content);
        step.SuspensionTimeoutSeconds = evt.TimeoutSeconds == 0 ? null : evt.TimeoutSeconds;
        step.RequestedVariableName = evt.VariableName ?? string.Empty;
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
        // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
        //   Old pattern: WorkflowSuspendedEvent.Metadata fallback for variable/secure/redacted_output reserved keys.
        //   New principle: typed suspension fields are the single source; Metadata is open extension data only.
        var timelineMetadata = BuildWorkflowSuspendedTimelineMetadata(evt);
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.suspended",
            $"{evt.StepId} ({evt.SuspensionType})",
            readModel.RootActorId,
            evt.StepId,
            step.StepType,
            eventType,
            timelineMetadata);
    }

    private static Dictionary<string, string> BuildWorkflowSuspendedTimelineMetadata(WorkflowSuspendedEvent evt)
    {
        var metadata = FilterOpenExtensionMetadata(evt.Metadata);
        var variableName = !string.IsNullOrWhiteSpace(evt.VariableName) ? evt.VariableName : string.Empty;
        var secure = evt.Secure;
        var redactedOutput = !string.IsNullOrWhiteSpace(evt.RedactedOutput) ? evt.RedactedOutput : string.Empty;

        if (!string.IsNullOrWhiteSpace(variableName))
            metadata["variable"] = variableName;
        if (secure)
            metadata["secure"] = "true";
        if (!string.IsNullOrWhiteSpace(redactedOutput))
            metadata["redacted_output"] = redactedOutput;

        return metadata;
    }

    private static Dictionary<string, string> FilterOpenExtensionMetadata(IDictionary<string, string> metadata)
    {
        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (key is "variable" or "secure" or "input_mode" or "redacted_output")
                continue;

            filtered[key] = value;
        }

        return filtered;
    }

    private static void ApplyWaitingForSignal(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WaitingForSignalEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "signal.waiting",
            SanitizeAuditText(evt.SignalName),
            readModel.RootActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
                ["timeout_ms"] = evt.TimeoutMs.ToString(),
            });
    }

    private static void ApplyWorkflowSignalBuffered(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowSignalBufferedEvent evt,
        DateTimeOffset observedAt)
    {
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "signal.buffered",
            SanitizeAuditText(evt.SignalName),
            readModel.RootActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
            });
    }

    private static void ApplyWorkflowRoleActorLinked(
        WorkflowRunInsightReportDocument readModel,
        WorkflowRoleActorLinkedEvent evt)
    {
        // Topology is materialized from committed link events, not runtime children.
        UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
    }

    private static void ApplySubWorkflowBindingUpserted(
        WorkflowRunInsightReportDocument readModel,
        SubWorkflowBindingUpsertedEvent evt)
    {
        // Topology is materialized from committed link events, not runtime children.
        UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
    }

    private static void ApplyWorkflowRoleReplyRecorded(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRoleReplyRecordedEvent evt,
        DateTimeOffset observedAt)
    {
        var roleId = string.IsNullOrWhiteSpace(evt.RoleId) ? evt.RoleActorId : evt.RoleId;
        var content = SanitizeAuditText(evt.Content);
        readModel.RoleReplies.Add(new WorkflowExecutionRoleReply
        {
            Timestamp = observedAt,
            RoleId = roleId,
            SessionId = evt.SessionId ?? string.Empty,
            Content = content,
            ContentLength = content.Length,
        });
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "role.reply",
            roleId,
            evt.RoleActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            });

        foreach (var toolCall in evt.ToolCalls)
        {
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "tool.call",
                toolCall.ToolName ?? string.Empty,
                evt.RoleActorId,
                null,
                null,
                eventType,
                BuildToolCallTimelineData(toolCall));
        }
    }

    private static Dictionary<string, string> BuildToolCallTimelineData(WorkflowRoleReplyToolCall toolCall)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["call_id"] = SanitizeAuditText(toolCall.CallId),
            ["success"] = toolCall.Success ? "true" : "false",
        };

        if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
            data["arguments_json"] = SanitizeAuditText(toolCall.ArgumentsJson);
        if (!string.IsNullOrEmpty(toolCall.ResultJson))
            data["result_json"] = SanitizeAuditText(toolCall.ResultJson);
        if (!string.IsNullOrEmpty(toolCall.Error))
            data["error"] = SanitizeAuditText(toolCall.Error);

        return data;
    }

    private static void ApplyWorkflowCompleted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = evt.Success
            ? WorkflowExecutionCompletionStatus.Completed
            : WorkflowExecutionCompletionStatus.Failed;
        readModel.Success = evt.Success;
        readModel.FinalOutput = SanitizeAuditText(evt.Output);
        readModel.FinalError = SanitizeAuditText(evt.Error);
        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            evt.Success ? "workflow.completed" : "workflow.failed",
            evt.Success ? "completed" : "failed",
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static void ApplyWorkflowStopped(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;
        readModel.Success = false;
        readModel.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            readModel.FinalError = SanitizeAuditText(evt.Reason);
        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.stopped",
            SanitizeAuditText(evt.Reason ?? "stopped"),
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static void ApplyWorkflowRunStopped(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRunStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;
        readModel.Success = false;
        readModel.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            readModel.FinalError = SanitizeAuditText(evt.Reason);
        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.stopped",
            SanitizeAuditText(evt.Reason ?? "stopped"),
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static WorkflowExecutionStepTrace GetOrCreateStep(
        IList<WorkflowExecutionStepTrace> steps,
        string? stepId)
    {
        var normalizedStepId = stepId ?? string.Empty;
        var existing = steps.FirstOrDefault(x => string.Equals(x.StepId, normalizedStepId, StringComparison.Ordinal));
        if (existing != null)
            return existing;

        existing = new WorkflowExecutionStepTrace
        {
            StepId = normalizedStepId,
        };
        steps.Add(existing);
        return existing;
    }

    private static string BuildTypeUrl(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return "type.googleapis.com/" + descriptor.FullName;
    }

    private static void UpsertTopology(
        IList<WorkflowExecutionTopologyEdge> topology,
        string parent,
        string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
            return;

        if (topology.Any(x =>
                string.Equals(x.Parent, parent, StringComparison.Ordinal) &&
                string.Equals(x.Child, child, StringComparison.Ordinal)))
        {
            return;
        }

        topology.Add(new WorkflowExecutionTopologyEdge(parent, child));
    }

    private static void AddTimeline(
        IList<WorkflowExecutionTimelineEvent> timeline,
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        IEnumerable<KeyValuePair<string, string>>? data)
    {
        timeline.Add(new WorkflowExecutionTimelineEvent
        {
            Timestamp = timestamp,
            Stage = stage,
            Message = message,
            AgentId = agentId ?? string.Empty,
            StepId = stepId ?? string.Empty,
            StepType = stepType ?? string.Empty,
            EventType = eventType ?? string.Empty,
            Data = SanitizeAuditMap(data),
        });
    }

    private static WorkflowExecutionTimelineEvent CloneTimelineEvent(WorkflowExecutionTimelineEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionTimelineEvent
        {
            Timestamp = source.Timestamp,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = source.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        };
    }

    private static WorkflowExecutionStepTrace CloneStepTrace(WorkflowExecutionStepTrace source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionStepTrace
        {
            StepId = source.StepId,
            StepType = source.StepType,
            TargetRole = source.TargetRole,
            RequestedAt = source.RequestedAt,
            CompletedAt = source.CompletedAt,
            Success = source.Success,
            WorkerId = source.WorkerId,
            OutputPreview = source.OutputPreview,
            Error = source.Error,
            RequestParameters = source.RequestParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            CompletionAnnotations = source.CompletionAnnotations.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionContent = source.SuspensionContent,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
            RequestedVariableName = source.RequestedVariableName,
            Usage = CloneUsage(source.Usage),
        };
    }

    private static void RefreshSummary(WorkflowRunInsightReportDocument readModel)
    {
        var summary = readModel.Summary;
        summary.TotalSteps = readModel.Steps.Count;
        summary.RequestedSteps = readModel.Steps.Count(x => x.RequestedAt.HasValue);
        summary.CompletedSteps = readModel.Steps.Count(x => x.CompletedAt.HasValue);
        summary.RoleReplyCount = readModel.RoleReplies.Count;
        summary.StepTypeCounts.Clear();
        foreach (var group in readModel.Steps
                     .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
                     .GroupBy(x => x.StepType, StringComparer.OrdinalIgnoreCase))
        {
            summary.StepTypeCounts[group.Key] = group.Count();
        }

        readModel.Usage = BuildRunUsage(readModel.Steps);
    }

    private static WorkflowUsageMetricsReadModel ToReadModelUsage(WorkflowUsageMetrics? usage) =>
        usage == null
            ? new WorkflowUsageMetricsReadModel()
            : new WorkflowUsageMetricsReadModel
            {
                PromptTokens = Math.Max(0, usage.PromptTokens),
                CompletionTokens = Math.Max(0, usage.CompletionTokens),
                TotalTokens = Math.Max(0, usage.TotalTokens),
                Model = usage.Model ?? string.Empty,
                Cost = Math.Max(0, usage.Cost),
                LatencyMs = Math.Max(0, usage.LatencyMs),
            };

    private static WorkflowUsageMetricsReadModel CloneUsage(WorkflowUsageMetricsReadModel? usage) =>
        usage == null
            ? new WorkflowUsageMetricsReadModel()
            : new WorkflowUsageMetricsReadModel
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = usage.Model ?? string.Empty,
                Cost = usage.Cost,
                LatencyMs = usage.LatencyMs,
            };

    private static WorkflowUsageMetricsReadModel BuildRunUsage(IEnumerable<WorkflowExecutionStepTrace> steps)
    {
        var result = new WorkflowUsageMetricsReadModel();
        foreach (var step in steps)
        {
            var usage = step.Usage;
            result.PromptTokens += Math.Max(0, usage.PromptTokens);
            result.CompletionTokens += Math.Max(0, usage.CompletionTokens);
            result.TotalTokens += Math.Max(0, usage.TotalTokens);
            if (!string.IsNullOrWhiteSpace(usage.Model))
                result.Model = usage.Model;
            result.Cost += Math.Max(0, usage.Cost);
            result.LatencyMs += Math.Max(0, usage.LatencyMs);
        }

        return result;
    }

    private static string ResolveWorkflowName(
        WorkflowRunState state,
        string? existingWorkflowName)
    {
        if (!string.IsNullOrWhiteSpace(state.WorkflowName))
            return state.WorkflowName;

        return existingWorkflowName ?? string.Empty;
    }

    private static WorkflowExecutionCompletionStatus ResolveCompletionStatus(
        string? status,
        WorkflowExecutionCompletionStatus existing)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "completed" => WorkflowExecutionCompletionStatus.Completed,
            "failed" => WorkflowExecutionCompletionStatus.Failed,
            "stopped" => WorkflowExecutionCompletionStatus.Stopped,
            "running" => existing == WorkflowExecutionCompletionStatus.WaitingForSignal
                ? WorkflowExecutionCompletionStatus.WaitingForSignal
                : WorkflowExecutionCompletionStatus.Running,
            _ => existing == default ? WorkflowExecutionCompletionStatus.Unknown : existing,
        };
    }

    private static bool? ResolveSuccess(string? status) =>
        (status ?? string.Empty).Trim() switch
        {
            "completed" => true,
            "failed" => false,
            "stopped" => false,
            _ => null,
        };

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static void ReplaceMap(
        IDictionary<string, string> target,
        IEnumerable<KeyValuePair<string, string>> source)
    {
        target.Clear();
        foreach (var (key, value) in source)
            target[SanitizeAuditText(key)] = SanitizeAuditValue(key, value);
    }

    private static string SanitizeAuditText(string? value) =>
        WorkflowAuditTextSanitizer.Sanitize(value);

    private static string SanitizeAuditValue(string? key, string? value) =>
        WorkflowAuditTextSanitizer.SanitizeValue(key, value);

    private static string SanitizeAuditTextForDisplay(string? value, int maxLen) =>
        WorkflowAuditTextSanitizer.SanitizeForDisplay(value, maxLen);

    private static Dictionary<string, string> SanitizeAuditMap(IEnumerable<KeyValuePair<string, string>>? data) =>
        WorkflowAuditTextSanitizer.SanitizeMap(data);
}
