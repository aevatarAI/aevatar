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
            LastUpdatedAt = source.EndedAt,
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
}
