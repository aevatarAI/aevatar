using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowExecutionReadModelMapper
{
    public WorkflowActorSnapshot ToActorSnapshot(WorkflowExecutionReport source)
    {
        return new WorkflowActorSnapshot
        {
            ActorId = source.RootActorId,
            WorkflowName = source.WorkflowName,
            LastCommandId = source.CommandId,
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId,
            LastUpdatedAt = source.UpdatedAt,
            LastSuccess = source.Success,
            LastOutput = source.FinalOutput,
            LastError = source.FinalError,
            TotalSteps = source.Summary.TotalSteps,
            RequestedSteps = source.Summary.RequestedSteps,
            CompletedSteps = source.Summary.CompletedSteps,
            RoleReplyCount = source.Summary.RoleReplyCount,
        };
    }

    public WorkflowActorTimelineItem ToActorTimelineItem(WorkflowExecutionTimelineEvent source)
    {
        return new WorkflowActorTimelineItem
        {
            Timestamp = source.Timestamp,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = new Dictionary<string, string>(source.Data, StringComparer.Ordinal),
        };
    }

    public WorkflowActorGraphNode ToActorGraphNode(ProjectionGraphNode source)
    {
        return new WorkflowActorGraphNode
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            UpdatedAt = source.UpdatedAt,
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
    }

    public WorkflowActorGraphEdge ToActorGraphEdge(ProjectionGraphEdge source)
    {
        return new WorkflowActorGraphEdge
        {
            EdgeId = source.EdgeId,
            FromNodeId = source.FromNodeId,
            ToNodeId = source.ToNodeId,
            EdgeType = source.EdgeType,
            UpdatedAt = source.UpdatedAt,
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
    }

    public WorkflowActorGraphSubgraph ToActorGraphSubgraph(
        string rootNodeId,
        ProjectionGraphSubgraph source)
    {
        return new WorkflowActorGraphSubgraph
        {
            RootNodeId = rootNodeId,
            Nodes = source.Nodes.Select(ToActorGraphNode).ToList(),
            Edges = source.Edges.Select(ToActorGraphEdge).ToList(),
        };
    }
}
