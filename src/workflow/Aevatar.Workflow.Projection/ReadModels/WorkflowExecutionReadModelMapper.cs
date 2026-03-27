using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowExecutionReadModelMapper
{
    public WorkflowActorSnapshot ToActorSnapshot(WorkflowExecutionCurrentStateDocument source)
    {
        return new WorkflowActorSnapshot
        {
            ActorId = source.RootActorId,
            WorkflowName = source.WorkflowName,
            LastCommandId = source.CommandId,
            CompletionStatus = MapCompletionStatus(source.Status),
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId,
            LastUpdatedAt = source.UpdatedAt,
            LastSuccess = source.Success,
            LastOutput = source.FinalOutput,
            LastError = source.FinalError,
            TotalSteps = 0,
            RequestedSteps = 0,
            CompletedSteps = 0,
            RoleReplyCount = 0,
        };
    }

    public WorkflowActorSnapshot ToActorSnapshot(
        WorkflowExecutionCurrentStateDocument source,
        WorkflowRunInsightReportDocument? report)
    {
        var snapshot = ToActorSnapshot(source);
        if (report == null)
            return snapshot;

        snapshot.WorkflowName = string.IsNullOrWhiteSpace(snapshot.WorkflowName)
            ? report.WorkflowName
            : snapshot.WorkflowName;
        snapshot.CompletionStatus = MapCompletionStatus(report.CompletionStatus);
        snapshot.LastSuccess = report.Success;
        snapshot.LastOutput = string.IsNullOrWhiteSpace(snapshot.LastOutput)
            ? report.FinalOutput
            : snapshot.LastOutput;
        snapshot.LastError = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? report.FinalError
            : snapshot.LastError;
        snapshot.TotalSteps = report.Summary.TotalSteps;
        snapshot.RequestedSteps = report.Summary.RequestedSteps;
        snapshot.CompletedSteps = report.Summary.CompletedSteps;
        snapshot.RoleReplyCount = report.Summary.RoleReplyCount;
        return snapshot;
    }

    public WorkflowActorProjectionState ToActorProjectionState(WorkflowExecutionCurrentStateDocument source)
    {
        return new WorkflowActorProjectionState
        {
            ActorId = source.RootActorId,
            LastCommandId = source.CommandId,
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId,
            LastUpdatedAt = source.UpdatedAt,
        };
    }

    public WorkflowRunReport ToRunReport(WorkflowRunInsightReportDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowRunReport
        {
            ReportVersion = source.ReportVersion,
            ProjectionScope = MapProjectionScope(source.ProjectionScope),
            TopologySource = MapTopologySource(source.TopologySource),
            CompletionStatus = MapCompletionStatus(source.CompletionStatus),
            WorkflowName = source.WorkflowName,
            RootActorId = source.RootActorId,
            CommandId = source.CommandId,
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            StartedAt = source.StartedAt,
            EndedAt = source.EndedAt,
            DurationMs = source.DurationMs,
            Success = source.Success,
            Input = source.Input,
            FinalOutput = source.FinalOutput,
            FinalError = source.FinalError,
            Topology = source.Topology
                .Select(edge => new WorkflowRunTopologyEdge(edge.Parent, edge.Child))
                .ToList(),
            Steps = source.Steps.Select(MapStepTrace).ToList(),
            RoleReplies = source.RoleReplies.Select(MapRoleReply).ToList(),
            Timeline = source.Timeline.Select(MapTimelineEvent).ToList(),
            Summary = MapSummary(source.Summary),
        };
    }

    public WorkflowActorTimelineItem ToActorTimelineItem(WorkflowExecutionTimelineEvent source)
    {
        var item = new WorkflowActorTimelineItem
        {
            Timestamp = source.Timestamp,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
        };
        item.Data.Add(source.Data);
        return item;
    }

    public WorkflowActorGraphNode ToActorGraphNode(ProjectionGraphNode source)
    {
        var node = new WorkflowActorGraphNode
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            UpdatedAt = source.UpdatedAt,
        };
        node.Properties.Add(source.Properties);
        return node;
    }

    public WorkflowActorGraphEdge ToActorGraphEdge(ProjectionGraphEdge source)
    {
        var edge = new WorkflowActorGraphEdge
        {
            EdgeId = source.EdgeId,
            FromNodeId = source.FromNodeId,
            ToNodeId = source.ToNodeId,
            EdgeType = source.EdgeType,
            UpdatedAt = source.UpdatedAt,
        };
        edge.Properties.Add(source.Properties);
        return edge;
    }

    public WorkflowActorGraphSubgraph ToActorGraphSubgraph(
        string rootNodeId,
        ProjectionGraphSubgraph source)
    {
        var subgraph = new WorkflowActorGraphSubgraph
        {
            RootNodeId = rootNodeId,
        };
        subgraph.Nodes.Add(source.Nodes.Select(ToActorGraphNode));
        subgraph.Edges.Add(source.Edges.Select(ToActorGraphEdge));
        return subgraph;
    }

    private static WorkflowRunCompletionStatus MapCompletionStatus(string? status)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "running" => WorkflowRunCompletionStatus.Running,
            "completed" => WorkflowRunCompletionStatus.Completed,
            "failed" => WorkflowRunCompletionStatus.Failed,
            "stopped" => WorkflowRunCompletionStatus.Stopped,
            "not_found" => WorkflowRunCompletionStatus.NotFound,
            "disabled" => WorkflowRunCompletionStatus.Disabled,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }
    private static WorkflowRunCompletionStatus MapCompletionStatus(
        WorkflowExecutionCompletionStatus status)
    {
        return status switch
        {
            WorkflowExecutionCompletionStatus.Running => WorkflowRunCompletionStatus.Running,
            WorkflowExecutionCompletionStatus.Completed => WorkflowRunCompletionStatus.Completed,
            WorkflowExecutionCompletionStatus.Failed => WorkflowRunCompletionStatus.Failed,
            WorkflowExecutionCompletionStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            WorkflowExecutionCompletionStatus.NotFound => WorkflowRunCompletionStatus.NotFound,
            WorkflowExecutionCompletionStatus.Disabled => WorkflowRunCompletionStatus.Disabled,
            WorkflowExecutionCompletionStatus.WaitingForSignal => WorkflowRunCompletionStatus.Running,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }

    private static WorkflowRunProjectionScope MapProjectionScope(WorkflowExecutionProjectionScope scope) =>
        scope switch
        {
            WorkflowExecutionProjectionScope.ActorShared => WorkflowRunProjectionScope.ActorShared,
            WorkflowExecutionProjectionScope.RunIsolated => WorkflowRunProjectionScope.RunIsolated,
            _ => WorkflowRunProjectionScope.Unknown,
        };

    private static WorkflowRunTopologySource MapTopologySource(WorkflowExecutionTopologySource source) =>
        source switch
        {
            WorkflowExecutionTopologySource.RuntimeSnapshot => WorkflowRunTopologySource.RuntimeSnapshot,
            _ => WorkflowRunTopologySource.Unknown,
        };

    private static WorkflowRunStepTrace MapStepTrace(WorkflowExecutionStepTrace source) =>
        new()
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
            RequestParameters = source.RequestParametersMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            CompletionAnnotations = source.CompletionAnnotationsMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSecondsValue == 0 ? null : source.SuspensionTimeoutSecondsValue,
            RequestedVariableName = source.RequestedVariableName,
        };

    private static WorkflowRunRoleReply MapRoleReply(WorkflowExecutionRoleReply source) =>
        new()
        {
            Timestamp = source.TimestampUtcValue?.ToDateTimeOffset() ?? default,
            RoleId = source.RoleId,
            SessionId = source.SessionId,
            Content = source.Content,
            ContentLength = source.ContentLength,
        };

    private static WorkflowRunTimelineEvent MapTimelineEvent(WorkflowExecutionTimelineEvent source) =>
        new()
        {
            Timestamp = source.TimestampUtcValue?.ToDateTimeOffset() ?? default,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = source.DataMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        };

    private static WorkflowRunStatistics MapSummary(WorkflowExecutionSummary? source) =>
        source == null
            ? new WorkflowRunStatistics()
            : new WorkflowRunStatistics
            {
                TotalSteps = source.TotalSteps,
                RequestedSteps = source.RequestedSteps,
                CompletedSteps = source.CompletedSteps,
                RoleReplyCount = source.RoleReplyCount,
                StepTypeCounts = source.StepTypeCountsMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            };
}
