using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Projectors;

internal static class WorkflowRunInsightProjectionMaps
{
    public static bool TryUnpack(
        EventEnvelope envelope,
        out StateEvent? stateEvent,
        out WorkflowRunInsightState? state)
    {
        if (CommittedStateEventEnvelope.TryUnpackState<WorkflowRunInsightState>(
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

    public static WorkflowExecutionReport ToReport(
        WorkflowRunInsightState source,
        StateEvent stateEvent)
    {
        var readModel = new WorkflowExecutionReport
        {
            Id = source.RootActorId,
            RootActorId = source.RootActorId,
            CommandId = source.CommandId,
            ReportVersion = string.IsNullOrWhiteSpace(source.ReportVersion) ? "2.0" : source.ReportVersion,
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
            CompletionStatus = MapCompletionStatus(source.CompletionStatus),
            WorkflowName = source.WorkflowName ?? string.Empty,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            StartedAt = source.StartedAt,
            EndedAt = source.EndedAt,
            Success = source.Success,
            Input = source.Input ?? string.Empty,
            FinalOutput = source.FinalOutput ?? string.Empty,
            FinalError = source.FinalError ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = source.SummaryValue?.TotalSteps ?? 0,
                RequestedSteps = source.SummaryValue?.RequestedSteps ?? 0,
                CompletedSteps = source.SummaryValue?.CompletedSteps ?? 0,
                RoleReplyCount = source.SummaryValue?.RoleReplyCount ?? 0,
                StepTypeCounts = source.SummaryValue?.StepTypeCountsMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase) ?? [],
            },
        };

        readModel.Topology = source.TopologyEntries
            .Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child))
            .ToList();
        readModel.Steps = source.StepEntries
            .Select(ToStepTrace)
            .ToList();
        readModel.RoleReplies = source.RoleReplyEntries
            .Select(x => new WorkflowExecutionRoleReply
            {
                Timestamp = x.TimestampUtcValue?.ToDateTimeOffset() ?? default,
                RoleId = x.RoleId,
                SessionId = x.SessionId,
                Content = x.Content,
                ContentLength = x.ContentLength,
            })
            .ToList();
        readModel.Timeline = source.TimelineEntries
            .Select(ToTimelineEvent)
            .ToList();

        return readModel;
    }

    public static WorkflowRunTimelineDocument ToTimelineDocument(
        WorkflowRunInsightState source,
        StateEvent stateEvent)
    {
        return new WorkflowRunTimelineDocument
        {
            Id = source.RootActorId,
            RootActorId = source.RootActorId,
            CommandId = source.CommandId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = source.UpdatedAt,
            Timeline =
            [
                .. source.TimelineEntries.Select(ToTimelineEvent),
            ],
        };
    }

    public static WorkflowRunGraphMirrorReadModel ToGraphMirrorReadModel(
        WorkflowRunInsightState source,
        StateEvent stateEvent)
    {
        return new WorkflowRunGraphMirrorReadModel
        {
            Id = source.RootActorId,
            RootActorId = source.RootActorId,
            CommandId = source.CommandId,
            WorkflowName = source.WorkflowName ?? string.Empty,
            Input = source.Input ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = source.UpdatedAt,
            Topology =
            [
                .. source.TopologyEntries.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)),
            ],
            Steps =
            [
                .. source.StepEntries.Select(ToStepTrace),
            ],
        };
    }

    private static WorkflowExecutionStepTrace ToStepTrace(WorkflowRunInsightStepTrace source)
    {
        return new WorkflowExecutionStepTrace
        {
            StepId = source.StepId,
            StepType = source.StepType,
            TargetRole = source.TargetRole,
            RequestedAt = source.RequestedAtUtcValue?.ToDateTimeOffset(),
            CompletedAt = source.CompletedAtUtcValue?.ToDateTimeOffset(),
            Success = source.SuccessWrapper,
            WorkerId = source.WorkerId,
            OutputPreview = source.OutputPreview,
            Error = source.Error,
            RequestParameters = source.RequestParametersMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            CompletionAnnotations = source.CompletionAnnotationsMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSecondsValue == 0 ? null : source.SuspensionTimeoutSecondsValue,
            RequestedVariableName = source.RequestedVariableName,
        };
    }

    private static WorkflowExecutionTimelineEvent ToTimelineEvent(WorkflowRunInsightTimelineEvent source)
    {
        return new WorkflowExecutionTimelineEvent
        {
            Timestamp = source.TimestampUtcValue?.ToDateTimeOffset() ?? default,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = source.DataMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    private static WorkflowExecutionCompletionStatus MapCompletionStatus(WorkflowRunInsightCompletionStatus status) =>
        status switch
        {
            WorkflowRunInsightCompletionStatus.Completed => WorkflowExecutionCompletionStatus.Completed,
            WorkflowRunInsightCompletionStatus.TimedOut => WorkflowExecutionCompletionStatus.TimedOut,
            WorkflowRunInsightCompletionStatus.Failed => WorkflowExecutionCompletionStatus.Failed,
            WorkflowRunInsightCompletionStatus.Stopped => WorkflowExecutionCompletionStatus.Stopped,
            WorkflowRunInsightCompletionStatus.NotFound => WorkflowExecutionCompletionStatus.NotFound,
            WorkflowRunInsightCompletionStatus.Disabled => WorkflowExecutionCompletionStatus.Disabled,
            WorkflowRunInsightCompletionStatus.WaitingForSignal => WorkflowExecutionCompletionStatus.WaitingForSignal,
            _ => WorkflowExecutionCompletionStatus.Running,
        };
}
