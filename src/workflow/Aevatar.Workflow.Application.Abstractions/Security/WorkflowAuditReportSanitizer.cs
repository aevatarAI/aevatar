using Aevatar.Workflow.Abstractions;
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
            Input = WorkflowAuditTextSanitizer.SanitizeForStorage(report.Input),
            FinalOutput = WorkflowAuditTextSanitizer.SanitizeForStorage(report.FinalOutput),
            FinalError = WorkflowAuditTextSanitizer.SanitizeForStorage(report.FinalError),
            CurrentWaitingSignal = SanitizeWaitingSignal(report.CurrentWaitingSignal),
            Topology = report.Topology.Select(SanitizeTopology).ToList(),
            Steps = report.Steps.Select(SanitizeStep).ToList(),
            RoleReplies = report.RoleReplies.Select(SanitizeRoleReply).ToList(),
            Operations = report.Operations.Select(SanitizeOperation).ToList(),
            Timeline = report.Timeline.Select(SanitizeTimelineEvent).ToList(),
            Usage = SanitizeUsage(report.Usage),
            Summary = SanitizeSummary(report.Summary),
        };
    }

    private static WorkflowRunWaitingSignal? SanitizeWaitingSignal(WorkflowRunWaitingSignal? signal) =>
        signal == null
            ? null
            : new WorkflowRunWaitingSignal
            {
                RunId = WorkflowAuditTextSanitizer.Sanitize(signal.RunId),
                StepId = WorkflowAuditTextSanitizer.Sanitize(signal.StepId),
                SignalName = WorkflowAuditTextSanitizer.Sanitize(signal.SignalName),
                Prompt = WorkflowAuditTextSanitizer.SanitizeForStorage(signal.Prompt),
                TimeoutMs = signal.TimeoutMs,
            };

    private static WorkflowRunTopologyEdge SanitizeTopology(WorkflowRunTopologyEdge edge) =>
        new(
            WorkflowAuditTextSanitizer.Sanitize(edge.Parent),
            WorkflowAuditTextSanitizer.Sanitize(edge.Child));

    private static WorkflowRunStepTrace SanitizeStep(WorkflowRunStepTrace step)
    {
        var failureOutput = SanitizeEvidenceText(step.FailureOutput, out var failureOutputTruncated);
        return new WorkflowRunStepTrace
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(step.StepId),
            DisplayName = WorkflowAuditTextSanitizer.Sanitize(step.DisplayName),
            StepType = WorkflowAuditTextSanitizer.Sanitize(step.StepType),
            TargetRole = WorkflowAuditTextSanitizer.Sanitize(step.TargetRole),
            RequestedAt = step.RequestedAt,
            CompletedAt = step.CompletedAt,
            Success = step.Success,
            WorkerId = WorkflowAuditTextSanitizer.Sanitize(step.WorkerId),
            OutputPreview = WorkflowAuditTextSanitizer.SanitizeForStorage(step.OutputPreview),
            Error = WorkflowAuditTextSanitizer.SanitizeForStorage(step.Error),
            FailureOutput = failureOutput,
            FailureOutputTruncated = step.FailureOutputTruncated || failureOutputTruncated,
            FailureOutcome = step.FailureOutcome,
            RecoveryFailureKind = step.RecoveryFailureKind,
            RetryDisposition = step.RetryDisposition,
            FileItemResults = SanitizeFileItemResults(step.FileItemResults),
            VoteAgreementDecision = SanitizeVoteAgreementDecision(step.VoteAgreementDecision),
            LatestFailedAttempt = SanitizeFailedAttempt(step.LatestFailedAttempt),
            RequestParameters = SanitizeStorageMap(step.RequestParameters),
            CompletionAnnotations = SanitizeStorageMap(step.CompletionAnnotations),
            NextStepId = WorkflowAuditTextSanitizer.Sanitize(step.NextStepId),
            BranchKey = WorkflowAuditTextSanitizer.Sanitize(step.BranchKey),
            AssignedVariable = WorkflowAuditTextSanitizer.Sanitize(step.AssignedVariable),
            AssignedValue = WorkflowAuditTextSanitizer.SanitizeValue(step.AssignedVariable, step.AssignedValue),
            SuspensionType = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionType),
            SuspensionPrompt = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionPrompt),
            SuspensionContent = WorkflowAuditTextSanitizer.Sanitize(step.SuspensionContent),
            SuspensionTimeoutSeconds = step.SuspensionTimeoutSeconds,
            RequestedVariableName = WorkflowAuditTextSanitizer.Sanitize(step.RequestedVariableName),
            ToolApproval = step.ToolApproval == null
                ? null
                : new WorkflowRunToolApproval
                {
                    ExecutionId = WorkflowAuditTextSanitizer.Sanitize(step.ToolApproval.ExecutionId),
                    ToolName = WorkflowAuditTextSanitizer.Sanitize(step.ToolApproval.ToolName),
                    ToolCallId = WorkflowAuditTextSanitizer.Sanitize(step.ToolApproval.ToolCallId),
                    ApprovalRequestId = WorkflowAuditTextSanitizer.Sanitize(step.ToolApproval.ApprovalRequestId),
                },
            Usage = SanitizeUsage(step.Usage),
            Outcome = step.Outcome,
        };
    }

    private static WorkflowRunFailedStepAttempt? SanitizeFailedAttempt(WorkflowRunFailedStepAttempt? source)
    {
        if (source == null)
            return null;

        var failureOutput = SanitizeEvidenceText(source.FailureOutput, out var failureOutputTruncated);
        return new WorkflowRunFailedStepAttempt
        {
            DisplayName = WorkflowAuditTextSanitizer.Sanitize(source.DisplayName),
            StepType = WorkflowAuditTextSanitizer.Sanitize(source.StepType),
            TargetRole = WorkflowAuditTextSanitizer.Sanitize(source.TargetRole),
            RequestedAt = source.RequestedAt,
            CompletedAt = source.CompletedAt,
            Success = source.Success,
            WorkerId = WorkflowAuditTextSanitizer.Sanitize(source.WorkerId),
            OutputPreview = WorkflowAuditTextSanitizer.SanitizeForStorage(source.OutputPreview),
            Error = WorkflowAuditTextSanitizer.SanitizeForStorage(source.Error),
            RequestParameters = SanitizeStorageMap(source.RequestParameters),
            CompletionAnnotations = SanitizeStorageMap(source.CompletionAnnotations),
            NextStepId = WorkflowAuditTextSanitizer.Sanitize(source.NextStepId),
            BranchKey = WorkflowAuditTextSanitizer.Sanitize(source.BranchKey),
            AssignedVariable = WorkflowAuditTextSanitizer.Sanitize(source.AssignedVariable),
            AssignedValue = WorkflowAuditTextSanitizer.SanitizeValue(source.AssignedVariable, source.AssignedValue),
            Usage = SanitizeUsage(source.Usage),
            FailureOutput = failureOutput,
            FailureOutputTruncated = source.FailureOutputTruncated || failureOutputTruncated,
            FailureOutcome = source.FailureOutcome,
            RecoveryFailureKind = source.RecoveryFailureKind,
            RetryDisposition = source.RetryDisposition,
            FileItemResults = SanitizeFileItemResults(source.FileItemResults),
            VoteAgreementDecision = SanitizeVoteAgreementDecision(source.VoteAgreementDecision),
            SuspensionType = WorkflowAuditTextSanitizer.Sanitize(source.SuspensionType),
            SuspensionPrompt = WorkflowAuditTextSanitizer.Sanitize(source.SuspensionPrompt),
            SuspensionContent = WorkflowAuditTextSanitizer.Sanitize(source.SuspensionContent),
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
            RequestedVariableName = WorkflowAuditTextSanitizer.Sanitize(source.RequestedVariableName),
            ToolApproval = source.ToolApproval == null
                ? null
                : new WorkflowRunToolApproval
                {
                    ExecutionId = WorkflowAuditTextSanitizer.Sanitize(source.ToolApproval.ExecutionId),
                    ToolName = WorkflowAuditTextSanitizer.Sanitize(source.ToolApproval.ToolName),
                    ToolCallId = WorkflowAuditTextSanitizer.Sanitize(source.ToolApproval.ToolCallId),
                    ApprovalRequestId = WorkflowAuditTextSanitizer.Sanitize(source.ToolApproval.ApprovalRequestId),
                },
        };
    }

    private static WorkflowFileItemResultSet? SanitizeFileItemResults(WorkflowFileItemResultSet? source)
    {
        if (source == null)
            return null;

        var sourceResultCount = Math.Max(0, source.SourceResultCount);
        if (!source.ResultsTruncated || sourceResultCount > 0)
            sourceResultCount = Math.Max(sourceResultCount, source.Results.Count);
        var resultsTruncated = source.ResultsTruncated || sourceResultCount > source.Results.Count;
        var retainedResults = SelectFileItemResultHeadTail(source.Results);
        resultsTruncated |= retainedResults.Count < source.Results.Count;

        var sanitized = new WorkflowFileItemResultSet
        {
            SourceResultCount = sourceResultCount,
            ResultsTruncated = resultsTruncated,
        };
        sanitized.Results.Add(retainedResults.Select(SanitizeFileItemResult));
        return sanitized;
    }

    private static IReadOnlyList<WorkflowFileItemResult> SelectFileItemResultHeadTail(
        IList<WorkflowFileItemResult> source)
    {
        var maxResults = WorkflowFileItemResultProjectionContract.MaxRetainedResults;
        if (source.Count <= maxResults)
            return source.ToList();

        var headCount = maxResults / 2;
        var tailCount = maxResults - headCount;
        return source.Take(headCount).Concat(source.Skip(source.Count - tailCount)).ToList();
    }

    private static WorkflowFileItemResult SanitizeFileItemResult(WorkflowFileItemResult item)
    {
        var output = WorkflowAuditTextSanitizer.SanitizeForStorage(
            item.Output,
            WorkflowFileItemResultProjectionContract.MaxEvidenceUtf8Bytes,
            out var outputTruncated);
        var error = WorkflowAuditTextSanitizer.SanitizeForStorage(
            item.Error,
            WorkflowFileItemResultProjectionContract.MaxEvidenceUtf8Bytes,
            out var errorTruncated);
        return new WorkflowFileItemResult
        {
            Index = item.Index,
            FileRef = SanitizeFileRef(item.FileRef),
            Success = item.Success,
            Output = output,
            Error = error,
            OutputTruncated = item.OutputTruncated || outputTruncated,
            ErrorTruncated = item.ErrorTruncated || errorTruncated,
        };
    }

    private static WorkflowFileRef? SanitizeFileRef(WorkflowFileRef? source) =>
        source == null
            ? null
            : new WorkflowFileRef
            {
                FileId = WorkflowAuditTextSanitizer.Sanitize(source.FileId),
                ArtifactId = WorkflowAuditTextSanitizer.Sanitize(source.ArtifactId),
                SourceKind = source.SourceKind,
                SourceMessageId = WorkflowAuditTextSanitizer.Sanitize(source.SourceMessageId),
                SourceResourceKey = WorkflowAuditTextSanitizer.Sanitize(source.SourceResourceKey),
                FileName = WorkflowAuditTextSanitizer.Sanitize(source.FileName),
                MediaType = WorkflowAuditTextSanitizer.Sanitize(source.MediaType),
                SizeBytes = source.SizeBytes,
                Sha256 = WorkflowAuditTextSanitizer.Sanitize(source.Sha256),
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = WorkflowAuditTextSanitizer.Sanitize(source.OwnerRunId),
                OwnerScopeId = WorkflowAuditTextSanitizer.Sanitize(source.OwnerScopeId),
            };

    private static VoteAgreementDecision? SanitizeVoteAgreementDecision(VoteAgreementDecision? source)
    {
        if (source == null)
            return null;

        var output = SanitizeEvidenceText(source.Output, out var outputTruncated);
        var reason = SanitizeEvidenceText(source.Reason, out var reasonTruncated);
        var sanitized = new VoteAgreementDecision
        {
            Kind = source.Kind,
            BranchKey = WorkflowAuditTextSanitizer.Sanitize(source.BranchKey),
            WinnerCandidateId = WorkflowAuditTextSanitizer.Sanitize(source.WinnerCandidateId),
            Output = output,
            Reason = reason,
            OutputTruncated = source.OutputTruncated || outputTruncated,
            ReasonTruncated = source.ReasonTruncated || reasonTruncated,
        };
        foreach (var (label, count) in source.LabelCounts)
            sanitized.LabelCounts[WorkflowAuditTextSanitizer.Sanitize(label)] = count;
        return sanitized;
    }

    private static string SanitizeEvidenceText(string? value, out bool truncated) =>
        WorkflowAuditTextSanitizer.SanitizeForStorage(
            value,
            WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes,
            out truncated);

    private static WorkflowRunRoleReply SanitizeRoleReply(WorkflowRunRoleReply reply)
    {
        var content = WorkflowAuditTextSanitizer.SanitizeForStorage(reply.Content);
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
            Message = WorkflowAuditTextSanitizer.SanitizeForStorage(timelineEvent.Message),
            AgentId = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.AgentId),
            StepId = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.StepId),
            StepType = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.StepType),
            EventType = WorkflowAuditTextSanitizer.Sanitize(timelineEvent.EventType),
            Data = SanitizeStorageMap(timelineEvent.Data),
        };

    private static Dictionary<string, string> SanitizeStorageMap(
        IEnumerable<KeyValuePair<string, string>>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source == null)
            return result;

        foreach (var (key, value) in source)
        {
            var sanitizedKey = WorkflowAuditTextSanitizer.Sanitize(key);
            var keyRedactedValue = WorkflowAuditTextSanitizer.SanitizeValue(key, value);
            result[sanitizedKey] = WorkflowAuditTextSanitizer.SanitizeForStorage(keyRedactedValue);
        }

        return result;
    }

    private static WorkflowRunOperation SanitizeOperation(WorkflowRunOperation operation) =>
        new()
        {
            SessionId = WorkflowAuditTextSanitizer.Sanitize(operation.SessionId),
            OperationId = WorkflowAuditTextSanitizer.Sanitize(operation.OperationId),
            ProgressSequence = operation.ProgressSequence,
            Round = operation.Round,
            Kind = operation.Kind,
            StartedAt = operation.StartedAt,
            CompletedAt = operation.CompletedAt,
            RoleActorId = WorkflowAuditTextSanitizer.Sanitize(operation.RoleActorId),
            Model = WorkflowAuditTextSanitizer.Sanitize(operation.Model),
            Provider = WorkflowAuditTextSanitizer.Sanitize(operation.Provider),
            InputSummary = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.InputSummary),
            AvailableToolNames = operation.AvailableToolNames
                .Select(WorkflowAuditTextSanitizer.Sanitize)
                .ToList(),
            Output = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.Output),
            ReasoningContent = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.ReasoningContent),
            FinishReason = WorkflowAuditTextSanitizer.Sanitize(operation.FinishReason),
            Usage = SanitizeUsage(operation.Usage),
            Success = operation.Success,
            Error = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.Error),
            ToolCallId = WorkflowAuditTextSanitizer.Sanitize(operation.ToolCallId),
            ToolName = WorkflowAuditTextSanitizer.Sanitize(operation.ToolName),
            ArgumentsJson = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.ArgumentsJson),
            ResultJson = WorkflowAuditTextSanitizer.SanitizeForStorage(operation.ResultJson),
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
