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

    public WorkflowActorRelationNode ToActorRelationNode(ProjectionRelationNode source)
    {
        return new WorkflowActorRelationNode
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            UpdatedAt = source.UpdatedAt,
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
    }

    public WorkflowActorRelationItem ToActorRelationItem(ProjectionRelationEdge source)
    {
        return new WorkflowActorRelationItem
        {
            EdgeId = source.EdgeId,
            FromNodeId = source.FromNodeId,
            ToNodeId = source.ToNodeId,
            RelationType = source.RelationType,
            UpdatedAt = source.UpdatedAt,
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
    }

    public WorkflowActorRelationSubgraph ToActorRelationSubgraph(
        string rootNodeId,
        ProjectionRelationSubgraph source)
    {
        return new WorkflowActorRelationSubgraph
        {
            RootNodeId = rootNodeId,
            Nodes = source.Nodes.Select(ToActorRelationNode).ToList(),
            Edges = source.Edges.Select(ToActorRelationItem).ToList(),
        };
    }
}
