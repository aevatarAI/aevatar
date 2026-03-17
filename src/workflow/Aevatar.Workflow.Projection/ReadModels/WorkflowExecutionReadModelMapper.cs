using Aevatar.Workflow.Application.Abstractions.Queries;

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
}
