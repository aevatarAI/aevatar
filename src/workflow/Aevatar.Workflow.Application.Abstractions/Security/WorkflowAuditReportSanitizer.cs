using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Security;

public static class WorkflowAuditReportSanitizer
{
    public static WorkflowRunReport Sanitize(WorkflowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new WorkflowRunReport
        {
            ReportVersion = WorkflowAuditTextSanitizer.Sanitize(report.ReportVersion),
            ProjectionScope = report.ProjectionScope,
            TopologySource = report.TopologySource,
            CompletionStatus = report.CompletionStatus,
            WorkflowName = WorkflowAuditTextSanitizer.Sanitize(report.WorkflowName),
            RootActorId = WorkflowAuditTextSanitizer.Sanitize(report.RootActorId),
            CommandId = WorkflowAuditTextSanitizer.Sanitize(report.CommandId),
            StateVersion = report.StateVersion,
            LastEventId = WorkflowAuditTextSanitizer.Sanitize(report.LastEventId),
            CreatedAt = report.CreatedAt,
            UpdatedAt = report.UpdatedAt,
            StartedAt = report.StartedAt,
            EndedAt = report.EndedAt,
            DurationMs = report.DurationMs,
            Success = report.Success,
            Input = WorkflowAuditTextSanitizer.Sanitize(report.Input),
            FinalOutput = WorkflowAuditTextSanitizer.Sanitize(report.FinalOutput),
            FinalError = WorkflowAuditTextSanitizer.Sanitize(report.FinalError),
            Topology = report.Topology.Select(SanitizeTopology).ToList(),
            Steps = report.Steps.Select(SanitizeStep).ToList(),
            RoleReplies = report.RoleReplies.Select(SanitizeRoleReply).ToList(),
            Timeline = report.Timeline.Select(SanitizeTimelineEvent).ToList(),
            Usage = SanitizeUsage(report.Usage),
            Summary = SanitizeSummary(report.Summary),
        };
    }

    private static WorkflowRunTopologyEdge SanitizeTopology(WorkflowRunTopologyEdge edge) =>
        new(
            WorkflowAuditTextSanitizer.Sanitize(edge.Parent),
            WorkflowAuditTextSanitizer.Sanitize(edge.Child));

    private static WorkflowRunStepTrace SanitizeStep(WorkflowRunStepTrace step) =>
        new()
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(step.StepId),
            StepType = WorkflowAuditTextSanitizer.Sanitize(step.StepType),
            TargetRole = WorkflowAuditTextSanitizer.Sanitize(step.TargetRole),
            RequestedAt = step.RequestedAt,
            CompletedAt = step.CompletedAt,
            Success = step.Success,
            WorkerId = WorkflowAuditTextSanitizer.Sanitize(step.WorkerId),
            OutputPreview = WorkflowAuditTextSanitizer.Sanitize(step.OutputPreview),
            Error = WorkflowAuditTextSanitizer.Sanitize(step.Error),
            RequestParameters = WorkflowAuditTextSanitizer.SanitizeMap(step.RequestParameters),
            CompletionAnnotations = WorkflowAuditTextSanitizer.SanitizeMap(step.CompletionAnnotations),
            NextStepId = WorkflowAuditTextSanitizer.Sanitize(step.NextStepId),
            BranchKey = WorkflowAuditTextSanitizer.Sanitize(step.BranchKey),
            AssignedVariable = WorkflowAuditTextSanitizer.Sanitize(step.AssignedVariable),
            AssignedValue = WorkflowAuditTextSanitizer.SanitizeValue(step.AssignedVariable, step.AssignedValue),
            SuspensionType = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionType),
            SuspensionPrompt = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionPrompt),
            SuspensionContent = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionContent),
            SuspensionTimeoutSeconds = step.SuspensionTimeoutSeconds,
            RequestedVariableName = WorkflowAuditTextSanitizer.Sanitize(step.RequestedVariableName),
            Usage = SanitizeUsage(step.Usage),
        };

    private static WorkflowRunRoleReply SanitizeRoleReply(WorkflowRunRoleReply reply)
    {
        var content = WorkflowAuditTextSanitizer.Sanitize(reply.Content);
        return new WorkflowRunRoleReply
        {
            Timestamp = reply.Timestamp,
            RoleId = WorkflowAuditTextSanitizer.Sanitize(reply.RoleId),
            SessionId = WorkflowAuditTextSanitizer.Sanitize(reply.SessionId),
            Content = content,
            ContentLength = content.Length,
        };
    }

    private static WorkflowRunTimelineEvent SanitizeTimelineEvent(WorkflowRunTimelineEvent timelineEvent) =>
        new()
        {
            Timestamp = timelineEvent.Timestamp,
            Stage = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.Stage),
            Message = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.Message),
            AgentId = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.AgentId),
            StepId = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.StepId),
            StepType = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.StepType),
            EventType = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.EventType),
            Data = WorkflowAuditTextSanitizer.SanitizeMap(timelineEvent.Data),
        };

    private static WorkflowRunUsageMetrics SanitizeUsage(WorkflowRunUsageMetrics? usage) =>
        usage == null
            ? new WorkflowRunUsageMetrics()
            : new WorkflowRunUsageMetrics
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = WorkflowAuditTextSanitizer.Sanitize(usage.Model),
                Cost = usage.Cost,
                LatencyMs = usage.LatencyMs,
            };

    private static WorkflowRunStatistics SanitizeSummary(WorkflowRunStatistics? summary) =>
        summary == null
            ? new WorkflowRunStatistics()
            : new WorkflowRunStatistics
            {
                TotalSteps = summary.TotalSteps,
                RequestedSteps = summary.RequestedSteps,
                CompletedSteps = summary.CompletedSteps,
                RoleReplyCount = summary.RoleReplyCount,
                StepTypeCounts = summary.StepTypeCounts.ToDictionary(
                    x => WorkflowAuditTextSanitizer.Sanitize(x.Key),
                    x => x.Value,
                    StringComparer.Ordinal),
            };
}
