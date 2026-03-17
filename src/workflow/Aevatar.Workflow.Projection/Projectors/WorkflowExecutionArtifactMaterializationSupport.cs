using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;

namespace Aevatar.Workflow.Projection.Projectors;

internal static class WorkflowExecutionArtifactMaterializationSupport
{
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
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
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
        readModel.Input = state.Input ?? string.Empty;
        readModel.FinalOutput = state.FinalOutput ?? string.Empty;
        readModel.FinalError = state.FinalError ?? string.Empty;
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

        if (payload.Is(WorkflowRunExecutionStartedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowRunExecutionStartedEvent>();
            readModel.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? readModel.WorkflowName : evt.WorkflowName;
            readModel.Input = evt.Input ?? string.Empty;
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
                payload.TypeUrl,
                null);
        }
        else if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            var step = GetOrCreateStep(readModel.Steps, evt.StepId);
            step.StepId = evt.StepId ?? string.Empty;
            step.StepType = evt.StepType ?? string.Empty;
            step.TargetRole = evt.TargetRole ?? string.Empty;
            step.RequestedAt = observedAt;
            ReplaceMap(step.RequestParameters, evt.Parameters);
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "step.request",
                $"{evt.StepId} ({evt.StepType})",
                readModel.RootActorId,
                evt.StepId,
                evt.StepType,
                payload.TypeUrl,
                evt.Parameters);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var step = GetOrCreateStep(readModel.Steps, evt.StepId);
            step.StepId = evt.StepId ?? string.Empty;
            step.CompletedAt = observedAt;
            step.Success = evt.Success;
            step.OutputPreview = Truncate(evt.Output ?? string.Empty, 240);
            step.Error = evt.Error ?? string.Empty;
            step.WorkerId = evt.WorkerId ?? string.Empty;
            step.NextStepId = evt.NextStepId ?? string.Empty;
            step.BranchKey = evt.BranchKey ?? string.Empty;
            step.AssignedVariable = evt.AssignedVariable ?? string.Empty;
            step.AssignedValue = evt.AssignedValue ?? string.Empty;
            ReplaceMap(step.CompletionAnnotations, evt.Annotations);
            AddTimeline(
                readModel.Timeline,
                observedAt,
                evt.Success ? "step.completed" : "step.failed",
                $"{evt.StepId} ({(evt.Success ? "success" : "failed")})",
                evt.WorkerId,
                evt.StepId,
                step.StepType,
                payload.TypeUrl,
                evt.Annotations);
        }
        else if (payload.Is(WorkflowSuspendedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowSuspendedEvent>();
            var step = GetOrCreateStep(readModel.Steps, evt.StepId);
            step.SuspensionType = evt.SuspensionType ?? string.Empty;
            step.SuspensionPrompt = evt.Prompt ?? string.Empty;
            step.SuspensionTimeoutSeconds = evt.TimeoutSeconds == 0 ? null : evt.TimeoutSeconds;
            step.RequestedVariableName = evt.VariableName ?? string.Empty;
            readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "workflow.suspended",
                $"{evt.StepId} ({evt.SuspensionType})",
                readModel.RootActorId,
                evt.StepId,
                step.StepType,
                payload.TypeUrl,
                evt.Metadata);
        }
        else if (payload.Is(WaitingForSignalEvent.Descriptor))
        {
            var evt = payload.Unpack<WaitingForSignalEvent>();
            readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "signal.waiting",
                evt.SignalName ?? string.Empty,
                readModel.RootActorId,
                evt.StepId,
                null,
                payload.TypeUrl,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["signal_name"] = evt.SignalName ?? string.Empty,
                    ["timeout_ms"] = evt.TimeoutMs.ToString(),
                });
        }
        else if (payload.Is(WorkflowSignalBufferedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowSignalBufferedEvent>();
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "signal.buffered",
                evt.SignalName ?? string.Empty,
                readModel.RootActorId,
                evt.StepId,
                null,
                payload.TypeUrl,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["signal_name"] = evt.SignalName ?? string.Empty,
                });
        }
        else if (payload.Is(WorkflowRoleActorLinkedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowRoleActorLinkedEvent>();
            UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
        }
        else if (payload.Is(SubWorkflowBindingUpsertedEvent.Descriptor))
        {
            var evt = payload.Unpack<SubWorkflowBindingUpsertedEvent>();
            UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
        }
        else if (payload.Is(WorkflowRoleReplyRecordedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowRoleReplyRecordedEvent>();
            readModel.RoleReplies.Add(new WorkflowExecutionRoleReply
            {
                Timestamp = observedAt,
                RoleId = string.IsNullOrWhiteSpace(evt.RoleId) ? evt.RoleActorId : evt.RoleId,
                SessionId = evt.SessionId ?? string.Empty,
                Content = evt.Content ?? string.Empty,
                ContentLength = (evt.Content ?? string.Empty).Length,
            });
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "role.reply",
                string.IsNullOrWhiteSpace(evt.RoleId) ? evt.RoleActorId : evt.RoleId,
                evt.RoleActorId,
                null,
                null,
                payload.TypeUrl,
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
                    payload.TypeUrl,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["call_id"] = toolCall.CallId ?? string.Empty,
                    });
            }
        }
        else if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowCompletedEvent>();
            readModel.CompletionStatus = evt.Success
                ? WorkflowExecutionCompletionStatus.Completed
                : WorkflowExecutionCompletionStatus.Failed;
            readModel.Success = evt.Success;
            readModel.FinalOutput = evt.Output ?? string.Empty;
            readModel.FinalError = evt.Error ?? string.Empty;
            readModel.EndedAt = observedAt;
            AddTimeline(
                readModel.Timeline,
                observedAt,
                evt.Success ? "workflow.completed" : "workflow.failed",
                evt.Success ? "completed" : "failed",
                readModel.RootActorId,
                null,
                null,
                payload.TypeUrl,
                null);
        }
        else if (payload.Is(WorkflowRunStoppedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowRunStoppedEvent>();
            readModel.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;
            if (!string.IsNullOrWhiteSpace(evt.Reason))
                readModel.FinalError = evt.Reason;
            readModel.EndedAt = observedAt;
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "workflow.stopped",
                evt.Reason ?? "stopped",
                readModel.RootActorId,
                null,
                null,
                payload.TypeUrl,
                null);
        }

        RefreshSummary(readModel);
    }

    public static WorkflowRunTimelineDocument BuildTimelineDocument(WorkflowRunInsightReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new WorkflowRunTimelineDocument
        {
            Id = report.Id,
            RootActorId = report.RootActorId,
            CommandId = report.CommandId,
            StateVersion = report.StateVersion,
            LastEventId = report.LastEventId,
            UpdatedAt = report.UpdatedAt,
            Timeline = report.Timeline.Select(CloneTimelineEvent).ToList(),
        };
    }

    public static WorkflowRunGraphArtifactDocument BuildGraphDocument(WorkflowRunInsightReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new WorkflowRunGraphArtifactDocument
        {
            Id = report.Id,
            RootActorId = report.RootActorId,
            CommandId = report.CommandId,
            WorkflowName = report.WorkflowName,
            Input = report.Input,
            StateVersion = report.StateVersion,
            LastEventId = report.LastEventId,
            UpdatedAt = report.UpdatedAt,
            Topology = report.Topology.Select(edge => new WorkflowExecutionTopologyEdge(edge.Parent, edge.Child)).ToList(),
            Steps = report.Steps.Select(CloneStepTrace).ToList(),
        };
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
            Data = data?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ?? [],
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
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
            RequestedVariableName = source.RequestedVariableName,
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
            target[key] = value;
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";
}
