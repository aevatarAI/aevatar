using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowExecutionReadModelMapper
{
    public WorkflowRunSummary ToSummary(WorkflowExecutionReport report)
    {
        return new WorkflowRunSummary(
            report.RunId,
            report.WorkflowName,
            report.RootActorId,
            report.StartedAt,
            report.EndedAt,
            report.DurationMs,
            report.Success,
            report.Summary.TotalSteps,
            MapProjectionScope(report.ProjectionScope),
            MapCompletionStatus(report.CompletionStatus));
    }

    public WorkflowRunReport ToReport(WorkflowExecutionReport source)
    {
        return new WorkflowRunReport
        {
            ReportVersion = source.ReportVersion,
            ProjectionScope = MapProjectionScope(source.ProjectionScope),
            TopologySource = MapTopologySource(source.TopologySource),
            CompletionStatus = MapCompletionStatus(source.CompletionStatus),
            WorkflowName = source.WorkflowName,
            RootActorId = source.RootActorId,
            RunId = source.RunId,
            StartedAt = source.StartedAt,
            EndedAt = source.EndedAt,
            DurationMs = source.DurationMs,
            Success = source.Success,
            Input = source.Input,
            FinalOutput = source.FinalOutput,
            FinalError = source.FinalError,
            Topology = source.Topology
                .Select(x => new WorkflowRunTopologyEdge(x.Parent, x.Child))
                .ToList(),
            Steps = source.Steps
                .Select(x => new WorkflowRunStepTrace
                {
                    StepId = x.StepId,
                    StepType = x.StepType,
                    RunId = x.RunId,
                    TargetRole = x.TargetRole,
                    RequestedAt = x.RequestedAt,
                    CompletedAt = x.CompletedAt,
                    Success = x.Success,
                    WorkerId = x.WorkerId,
                    OutputPreview = x.OutputPreview,
                    Error = x.Error,
                    RequestParameters = new Dictionary<string, string>(x.RequestParameters, StringComparer.Ordinal),
                    CompletionMetadata = new Dictionary<string, string>(x.CompletionMetadata, StringComparer.Ordinal),
                })
                .ToList(),
            RoleReplies = source.RoleReplies
                .Select(x => new WorkflowRunRoleReply
                {
                    Timestamp = x.Timestamp,
                    RoleId = x.RoleId,
                    MessageId = x.MessageId,
                    Content = x.Content,
                    ContentLength = x.ContentLength,
                })
                .ToList(),
            Timeline = source.Timeline
                .Select(x => new WorkflowRunTimelineEvent
                {
                    Timestamp = x.Timestamp,
                    Stage = x.Stage,
                    Message = x.Message,
                    AgentId = x.AgentId,
                    StepId = x.StepId,
                    StepType = x.StepType,
                    EventType = x.EventType,
                    Data = new Dictionary<string, string>(x.Data, StringComparer.Ordinal),
                })
                .ToList(),
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = source.Summary.TotalSteps,
                RequestedSteps = source.Summary.RequestedSteps,
                CompletedSteps = source.Summary.CompletedSteps,
                RoleReplyCount = source.Summary.RoleReplyCount,
                StepTypeCounts = new Dictionary<string, int>(source.Summary.StepTypeCounts, StringComparer.Ordinal),
            },
        };
    }

    private static WorkflowRunProjectionScope MapProjectionScope(WorkflowExecutionProjectionScope value)
    {
        return value switch
        {
            WorkflowExecutionProjectionScope.ActorShared => WorkflowRunProjectionScope.ActorShared,
            WorkflowExecutionProjectionScope.RunIsolated => WorkflowRunProjectionScope.RunIsolated,
            _ => WorkflowRunProjectionScope.Unknown,
        };
    }

    private static WorkflowRunTopologySource MapTopologySource(WorkflowExecutionTopologySource value)
    {
        return value switch
        {
            WorkflowExecutionTopologySource.RuntimeSnapshot => WorkflowRunTopologySource.RuntimeSnapshot,
            _ => WorkflowRunTopologySource.Unknown,
        };
    }

    private static WorkflowRunCompletionStatus MapCompletionStatus(WorkflowExecutionCompletionStatus value)
    {
        return value switch
        {
            WorkflowExecutionCompletionStatus.Running => WorkflowRunCompletionStatus.Running,
            WorkflowExecutionCompletionStatus.Completed => WorkflowRunCompletionStatus.Completed,
            WorkflowExecutionCompletionStatus.TimedOut => WorkflowRunCompletionStatus.TimedOut,
            WorkflowExecutionCompletionStatus.Failed => WorkflowRunCompletionStatus.Failed,
            WorkflowExecutionCompletionStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            WorkflowExecutionCompletionStatus.NotFound => WorkflowRunCompletionStatus.NotFound,
            WorkflowExecutionCompletionStatus.Disabled => WorkflowRunCompletionStatus.Disabled,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }
}
