using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowExecutionReadModelMapper
{
    public WorkflowActorSnapshot ToActorSnapshot(WorkflowExecutionCurrentStateDocument source)
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = source.RootActorId,
            RunId = source.RunId,
            WorkflowId = source.WorkflowId,
            WorkflowName = source.WorkflowName,
            ScopeId = source.ScopeId,
            RunOrigin = source.RunOrigin,
            LastCommandId = source.CommandId,
            CompletionStatus = MapCompletionStatus(source.Status),
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId,
            LastUpdatedAt = source.UpdatedAt,
            StartedAtUtc = source.StartedAtUtcValue,
            CompletedAtUtc = source.CompletedAtUtcValue,
            LastSuccess = source.Success,
            LastOutput = source.FinalOutput,
            LastError = source.FinalError,
            TerminalValueLifecycleFailureKind = source.TerminalValueLifecycleFailureKind,
            CompilationError = source.CompilationError,
            SagaStatus = source.SagaStatus,
            DeadLetterFailedCompensationStepId = source.DeadLetterFailedCompensationStepId,
            DeadLetterRemainingUncompensated = source.DeadLetterRemainingUncompensated,
            DeadLetterError = source.DeadLetterError,
            TotalSteps = 0,
            RequestedSteps = 0,
            CompletedSteps = 0,
            RoleReplyCount = 0,
            InputFileRefs = { source.InputFileRefs.Select(MapInputFileRef) },
            ConnectorApprovals = { source.ConnectorApprovals.Select(static approval => approval.Clone()) },
            ActivityInitiator = MapActivityInitiator(source.ActivityInitiator),
            InputSummary = source.InputSummary,
            ActivityCurrentStep = MapActivityCurrentStep(source.ActivityCurrentStep),
            ActivityFirstFailure = MapActivityFirstFailure(source.ActivityFirstFailure),
            ActivityWaiting = MapActivityWaiting(source.ActivityWaiting),
            RecoveryCapability = MapRecoveryCapability(source.RecoveryCapability),
            Lineage = MapLineage(source.Lineage),
        };
        if (source.HasDurationMs)
            snapshot.DurationMs = source.DurationMs;
        return snapshot;
    }

    private static WorkflowRunRecoveryCapability MapRecoveryCapability(
        WorkflowRunRecoveryCapabilityReadModel? source)
    {
        if (source == null)
            return new WorkflowRunRecoveryCapability();

        return new WorkflowRunRecoveryCapability
        {
            WorkflowDefinitionRevisionId = source.WorkflowDefinitionRevisionId ?? string.Empty,
            WorkflowDefinitionVersion = source.WorkflowDefinitionVersion,
            RetryFailedStep = MapRecoveryActionCapability(source.RetryFailedStep),
            RunAgain = MapRecoveryActionCapability(source.RunAgain),
        };
    }

    private static Aevatar.Workflow.Abstractions.WorkflowRunLineage MapLineage(
        Aevatar.Workflow.Abstractions.WorkflowRunLineage? source) =>
        source?.Clone() ?? new Aevatar.Workflow.Abstractions.WorkflowRunLineage
        {
            Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            UnavailableReason = "Run lineage is unavailable for this legacy run.",
            RetryFork = new Aevatar.Workflow.Abstractions.WorkflowRunRetryForkLineage
            {
                Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            },
            SubWorkflow = new Aevatar.Workflow.Abstractions.WorkflowRunSubWorkflowLineage
            {
                Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            },
        };

    private static WorkflowRecoveryActionCapability MapRecoveryActionCapability(
        WorkflowRecoveryActionCapabilityReadModel? source)
    {
        if (source == null)
            return new WorkflowRecoveryActionCapability
            {
                Eligibility = WorkflowRecoveryEligibility.Unavailable,
                UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable,
                UnavailableReason = "Recovery capability is unavailable for this legacy run.",
            };

        var capability = new WorkflowRecoveryActionCapability
        {
            Eligibility = MapRecoveryEligibility(source.Eligibility),
            UnavailableReasonCode = MapRecoveryUnavailableReasonCode(source.UnavailableReasonCode),
            UnavailableReason = source.UnavailableReason ?? string.Empty,
            StartingStepId = source.StartingStepId ?? string.Empty,
            ReusesPriorStepOutputs = source.ReusesPriorStepOutputs,
            MayIncurModelOrToolCost = source.MayIncurModelOrToolCost,
        };
        capability.RecommendedActions.Add(source.RecommendedActions.Select(MapRecoveryRecommendedAction));
        return capability;
    }

    private static WorkflowRecoveryEligibility MapRecoveryEligibility(
        WorkflowRecoveryEligibilityReadModel value) =>
        value switch
        {
            WorkflowRecoveryEligibilityReadModel.Eligible => WorkflowRecoveryEligibility.Eligible,
            WorkflowRecoveryEligibilityReadModel.Ineligible => WorkflowRecoveryEligibility.Ineligible,
            WorkflowRecoveryEligibilityReadModel.Unavailable => WorkflowRecoveryEligibility.Unavailable,
            _ => WorkflowRecoveryEligibility.Unspecified,
        };

    private static WorkflowRecoveryUnavailableReasonCode MapRecoveryUnavailableReasonCode(
        WorkflowRecoveryUnavailableReasonCodeReadModel value) =>
        value switch
        {
            WorkflowRecoveryUnavailableReasonCodeReadModel.None => WorkflowRecoveryUnavailableReasonCode.None,
            WorkflowRecoveryUnavailableReasonCodeReadModel.SourceRunNotTerminal => WorkflowRecoveryUnavailableReasonCode.SourceRunNotTerminal,
            WorkflowRecoveryUnavailableReasonCodeReadModel.MissingSourceFact => WorkflowRecoveryUnavailableReasonCode.MissingSourceFact,
            WorkflowRecoveryUnavailableReasonCodeReadModel.AuthorizationFailure => WorkflowRecoveryUnavailableReasonCode.AuthorizationFailure,
            WorkflowRecoveryUnavailableReasonCodeReadModel.ConfigurationFailure => WorkflowRecoveryUnavailableReasonCode.ConfigurationFailure,
            WorkflowRecoveryUnavailableReasonCodeReadModel.WorkflowDefinitionUnavailable => WorkflowRecoveryUnavailableReasonCode.WorkflowDefinitionUnavailable,
            WorkflowRecoveryUnavailableReasonCodeReadModel.LegacyUnavailable => WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable,
            _ => WorkflowRecoveryUnavailableReasonCode.Unspecified,
        };

    private static WorkflowRecoveryRecommendedAction MapRecoveryRecommendedAction(
        WorkflowRecoveryRecommendedActionReadModel value) =>
        value switch
        {
            WorkflowRecoveryRecommendedActionReadModel.Retry => WorkflowRecoveryRecommendedAction.Retry,
            WorkflowRecoveryRecommendedActionReadModel.RunAgain => WorkflowRecoveryRecommendedAction.RunAgain,
            WorkflowRecoveryRecommendedActionReadModel.FixAccess => WorkflowRecoveryRecommendedAction.FixAccess,
            WorkflowRecoveryRecommendedActionReadModel.ChangeConfiguration => WorkflowRecoveryRecommendedAction.ChangeConfiguration,
            WorkflowRecoveryRecommendedActionReadModel.EditWorkflow => WorkflowRecoveryRecommendedAction.EditWorkflow,
            WorkflowRecoveryRecommendedActionReadModel.EditInput => WorkflowRecoveryRecommendedAction.EditInput,
            WorkflowRecoveryRecommendedActionReadModel.TechnicalDetails => WorkflowRecoveryRecommendedAction.TechnicalDetails,
            _ => WorkflowRecoveryRecommendedAction.Unspecified,
        };

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
        var requestEvidenceById = source.RequestEvidenceById;

        return new WorkflowRunReport
        {
            ReportVersion = source.ReportVersion,
            ProjectionScope = MapProjectionScope(source.ProjectionScope),
            TopologySource = MapTopologySource(source.TopologySource),
            CompletionStatus = MapCompletionStatus(source),
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
            Steps = source.Steps.Select(step => MapStepTrace(step, requestEvidenceById)).ToList(),
            RoleReplies = source.RoleReplies.Select(MapRoleReply).ToList(),
            Operations = source.Operations.Select(MapOperation).ToList(),
            Timeline = source.Timeline.Select(item => MapTimelineEvent(item, requestEvidenceById)).ToList(),
            Usage = MapUsage(source.Usage),
            Summary = MapSummary(source.Summary),
        };
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: timeline mapper methods produced actor current-state timeline items.
    //   New principle: timeline mapper methods produce workflow-run export items from the report artifact.
    public WorkflowRunTimelineExportItem ToWorkflowRunTimelineExportItem(WorkflowExecutionTimelineEvent source)
        => ToWorkflowRunTimelineExportItem(source, requestEvidenceById: null);

    public WorkflowRunTimelineExportItem ToWorkflowRunTimelineExportItem(
        WorkflowExecutionTimelineEvent source,
        WorkflowRunInsightReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return ToWorkflowRunTimelineExportItem(source, report.RequestEvidenceById);
    }

    private static WorkflowRunTimelineExportItem ToWorkflowRunTimelineExportItem(
        WorkflowExecutionTimelineEvent source,
        IDictionary<string, WorkflowStepRequestEvidence>? requestEvidenceById)
    {
        var item = new WorkflowRunTimelineExportItem
        {
            Timestamp = source.Timestamp,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
        };
        item.Data.Add(ResolveRequestParameters(
            source.RequestEvidenceReference,
            source.DataMap,
            requestEvidenceById,
            source.StepId));
        return item;
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: graph mapper methods produced actor graph readmodel nodes.
    //   New principle: graph mapper methods produce workflow-run graph export nodes.
    public WorkflowRunGraphExportNode ToWorkflowRunGraphExportNode(ProjectionGraphNode source)
    {
        var node = new WorkflowRunGraphExportNode
        {
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            UpdatedAt = source.UpdatedAt,
        };
        node.Properties.Add(source.Properties);
        return node;
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: graph mapper methods produced actor graph readmodel edges.
    //   New principle: graph mapper methods produce workflow-run graph export edges.
    public WorkflowRunGraphExportEdge ToWorkflowRunGraphExportEdge(ProjectionGraphEdge source)
    {
        var edge = new WorkflowRunGraphExportEdge
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

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: graph mapper methods produced actor graph readmodel subgraphs.
    //   New principle: graph mapper methods produce workflow-run graph export subgraphs.
    public WorkflowRunGraphExportSubgraph ToWorkflowRunGraphExportSubgraph(
        string rootNodeId,
        ProjectionGraphSubgraph source,
        long sourceStateVersion = 0)
    {
        var subgraph = new WorkflowRunGraphExportSubgraph
        {
            RootNodeId = rootNodeId,
            SourceStateVersion = sourceStateVersion > 0
                ? sourceStateVersion
                : ResolveGraphSourceStateVersion(rootNodeId, source),
        };
        subgraph.Nodes.Add(source.Nodes.Select(ToWorkflowRunGraphExportNode));
        subgraph.Edges.Add(source.Edges.Select(ToWorkflowRunGraphExportEdge));
        return subgraph;
    }

    private static long ResolveGraphSourceStateVersion(string rootNodeId, ProjectionGraphSubgraph source)
    {
        var rootNodeIdValue = rootNodeId.Trim();
        var versions = source.Nodes
            .Where(node => string.Equals(node.NodeType, WorkflowExecutionGraphConstants.RunNodeType, StringComparison.Ordinal))
            .Where(node =>
                node.Properties.TryGetValue(WorkflowExecutionGraphConstants.RootActorIdPropertyKey, out var nodeRootActorId) &&
                string.Equals(nodeRootActorId, rootNodeIdValue, StringComparison.Ordinal))
            .Select(ReadSourceStateVersion)
            .Where(version => version > 0)
            .Distinct()
            .ToList();

        return versions.Count == 1 ? versions[0] : 0;
    }

    private static long ReadSourceStateVersion(ProjectionGraphNode node) =>
        node.Properties.TryGetValue(WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey, out var value) &&
        long.TryParse(value, out var parsed) &&
        parsed > 0
            ? parsed
            : 0;

    private static WorkflowRunCompletionStatus MapCompletionStatus(string? status)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "running" => WorkflowRunCompletionStatus.Running,
            "completed" => WorkflowRunCompletionStatus.Completed,
            "timed_out" => WorkflowRunCompletionStatus.TimedOut,
            "failed" => WorkflowRunCompletionStatus.Failed,
            "stopped" => WorkflowRunCompletionStatus.Stopped,
            "not_found" => WorkflowRunCompletionStatus.NotFound,
            "disabled" => WorkflowRunCompletionStatus.Disabled,
            "awaiting_tool_approval" => WorkflowRunCompletionStatus.AwaitingToolApproval,
            "waiting_for_signal" => WorkflowRunCompletionStatus.WaitingForSignal,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }

    private static WorkflowRunFileRef MapInputFileRef(WorkflowExecutionInputFileRefReadModel source) =>
        new()
        {
            FileId = source.FileId,
            ArtifactId = source.ArtifactId,
            SourceKindValue = source.SourceKindValue,
            SourceMessageId = source.SourceMessageId,
            SourceResourceKey = source.SourceResourceKey,
            FileName = source.FileName,
            MediaType = source.MediaType,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId,
            OwnerScopeId = source.OwnerScopeId,
        };

    private static WorkflowRunActivityInitiatorSnapshot MapActivityInitiator(
        WorkflowRunActivityInitiatorReadModel? source) =>
        source == null
            ? new WorkflowRunActivityInitiatorSnapshot { Availability = "unavailable", DisplayValue = "Unknown" }
            : new WorkflowRunActivityInitiatorSnapshot
            {
                Platform = source.Platform,
                Tenant = source.Tenant,
                ExternalUserId = source.ExternalUserId,
                Scope = source.Scope,
                BindingId = string.Empty,
                DisplayValue = string.IsNullOrWhiteSpace(source.DisplayValue) ? "Unknown" : source.DisplayValue,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowRunActivityStepSnapshot MapActivityCurrentStep(
        WorkflowRunActivityStepReadModel? source) =>
        source == null
            ? new WorkflowRunActivityStepSnapshot { Availability = "unavailable" }
            : new WorkflowRunActivityStepSnapshot
            {
                StepId = source.StepId,
                InputSummary = source.InputSummary,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowRunActivityFailureSnapshot MapActivityFirstFailure(
        WorkflowRunActivityFailureReadModel? source) =>
        source == null
            ? new WorkflowRunActivityFailureSnapshot { Availability = "unavailable" }
            : new WorkflowRunActivityFailureSnapshot
            {
                StepId = source.StepId,
                Message = source.Message,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowRunActivityWaitingSnapshot MapActivityWaiting(
        WorkflowRunActivityWaitingReadModel? source) =>
        source == null
            ? new WorkflowRunActivityWaitingSnapshot { Availability = "unavailable" }
            : new WorkflowRunActivityWaitingSnapshot
            {
                StepId = source.StepId,
                WaitingKind = source.WaitingKind,
                Prompt = source.Prompt,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowRunCompletionStatus MapCompletionStatus(
        WorkflowExecutionCompletionStatus status)
    {
        return status switch
        {
            WorkflowExecutionCompletionStatus.Running => WorkflowRunCompletionStatus.Running,
            WorkflowExecutionCompletionStatus.Completed => WorkflowRunCompletionStatus.Completed,
            WorkflowExecutionCompletionStatus.TimedOut => WorkflowRunCompletionStatus.TimedOut,
            WorkflowExecutionCompletionStatus.Failed => WorkflowRunCompletionStatus.Failed,
            WorkflowExecutionCompletionStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            WorkflowExecutionCompletionStatus.NotFound => WorkflowRunCompletionStatus.NotFound,
            WorkflowExecutionCompletionStatus.Disabled => WorkflowRunCompletionStatus.Disabled,
            WorkflowExecutionCompletionStatus.WaitingForSignal => WorkflowRunCompletionStatus.WaitingForSignal,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }

    private static WorkflowRunCompletionStatus MapCompletionStatus(
        WorkflowRunInsightReportDocument source)
    {
        if (source.CompletionStatus == WorkflowExecutionCompletionStatus.WaitingForSignal &&
            source.Steps.Any(static step =>
                step.ToolApprovalValue != null &&
                step.CompletedAtUtcValue == null))
        {
            return WorkflowRunCompletionStatus.AwaitingToolApproval;
        }

        return MapCompletionStatus(source.CompletionStatus);
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
            WorkflowExecutionTopologySource.CommittedProjection => WorkflowRunTopologySource.CommittedProjection,
            _ => WorkflowRunTopologySource.Unknown,
        };

    private static WorkflowRunStepTrace MapStepTrace(
        WorkflowExecutionStepTrace source,
        IDictionary<string, WorkflowStepRequestEvidence> requestEvidenceById) =>
        new()
        {
            StepId = source.StepId,
            DisplayName = source.DisplayName,
            StepType = source.StepType,
            TargetRole = source.TargetRole,
            RequestedAt = source.RequestedAtUtcValue?.ToDateTimeOffset(),
            CompletedAt = source.CompletedAtUtcValue?.ToDateTimeOffset(),
            Success = source.SuccessWrapper,
            WorkerId = source.WorkerId,
            OutputPreview = source.OutputPreview,
            Error = source.Error,
            FailureOutput = source.FailureOutput,
            FailureOutputTruncated = source.FailureOutputTruncated,
            FailureOutcome = source.FailureOutcome,
            RecoveryFailureKind = source.RecoveryFailureKind,
            RetryDisposition = source.RetryDisposition,
            FileItemResults = source.FileItemResults?.Clone(),
            VoteAgreementDecision = source.VoteAgreementDecision?.Clone(),
            LatestFailedAttempt = MapFailedStepAttempt(
                source.LatestFailedAttempt,
                source.StepId,
                requestEvidenceById),
            RequestParameters = ResolveRequestParameters(
                source.RequestEvidenceReference,
                source.RequestParametersMap,
                requestEvidenceById,
                source.StepId),
            CompletionAnnotations = source.CompletionAnnotationsMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionContent = source.SuspensionContent,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSecondsValue == 0 ? null : source.SuspensionTimeoutSecondsValue,
            RequestedVariableName = source.RequestedVariableName,
            ToolApproval = source.ToolApprovalValue == null
                ? null
                : new WorkflowRunToolApproval
                {
                    ExecutionId = source.ToolApprovalValue.ExecutionId,
                    ToolName = source.ToolApprovalValue.ToolName,
                    ToolCallId = source.ToolApprovalValue.ToolCallId,
                    ApprovalRequestId = source.ToolApprovalValue.ApprovalRequestId,
                },
            Usage = MapUsage(source.Usage),
            Outcome = MapStepOutcome(source.Outcome),
        };

    private static WorkflowRunFailedStepAttempt? MapFailedStepAttempt(
        WorkflowExecutionFailedStepAttemptReadModel? source,
        string stepId,
        IDictionary<string, WorkflowStepRequestEvidence> requestEvidenceById) =>
        source == null
            ? null
            : new WorkflowRunFailedStepAttempt
            {
                DisplayName = source.DisplayName,
                StepType = source.StepType,
                TargetRole = source.TargetRole,
                RequestedAt = source.RequestedAtUtcValue?.ToDateTimeOffset(),
                CompletedAt = source.CompletedAtUtcValue?.ToDateTimeOffset(),
                Success = source.SuccessWrapper,
                WorkerId = source.WorkerId,
                OutputPreview = source.OutputPreview,
                Error = source.Error,
                RequestParameters = ResolveRequestParameters(
                    source.RequestEvidenceReference,
                    source.RequestParametersMap,
                    requestEvidenceById,
                    stepId),
                CompletionAnnotations = source.CompletionAnnotationsMap.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.Ordinal),
                NextStepId = source.NextStepId,
                BranchKey = source.BranchKey,
                AssignedVariable = source.AssignedVariable,
                AssignedValue = source.AssignedValue,
                Usage = MapUsage(source.Usage),
                FailureOutput = source.FailureOutput,
                FailureOutputTruncated = source.FailureOutputTruncated,
                FailureOutcome = source.FailureOutcome,
                RecoveryFailureKind = source.RecoveryFailureKind,
                RetryDisposition = source.RetryDisposition,
                FileItemResults = source.FileItemResults?.Clone(),
                VoteAgreementDecision = source.VoteAgreementDecision?.Clone(),
                SuspensionType = source.SuspensionType,
                SuspensionPrompt = source.SuspensionPrompt,
                SuspensionContent = source.SuspensionContent,
                SuspensionTimeoutSeconds = source.SuspensionTimeoutSecondsValue == 0
                    ? null
                    : source.SuspensionTimeoutSecondsValue,
                RequestedVariableName = source.RequestedVariableName,
                ToolApproval = source.ToolApprovalValue == null
                    ? null
                    : new WorkflowRunToolApproval
                    {
                        ExecutionId = source.ToolApprovalValue.ExecutionId,
                        ToolName = source.ToolApprovalValue.ToolName,
                        ToolCallId = source.ToolApprovalValue.ToolCallId,
                        ApprovalRequestId = source.ToolApprovalValue.ApprovalRequestId,
                    },
            };

    private static WorkflowRunStepOutcome MapStepOutcome(WorkflowExecutionStepOutcomeReadModel outcome) =>
        outcome switch
        {
            WorkflowExecutionStepOutcomeReadModel.Succeeded => WorkflowRunStepOutcome.Succeeded,
            WorkflowExecutionStepOutcomeReadModel.Failed => WorkflowRunStepOutcome.Failed,
            WorkflowExecutionStepOutcomeReadModel.Waiting => WorkflowRunStepOutcome.Waiting,
            WorkflowExecutionStepOutcomeReadModel.Skipped => WorkflowRunStepOutcome.Skipped,
            _ => WorkflowRunStepOutcome.Unspecified,
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

    private static WorkflowRunOperation MapOperation(WorkflowRuntimeOperationReadModel source) =>
        new()
        {
            SessionId = source.SessionId,
            OperationId = source.OperationId,
            ProgressSequence = source.ProgressSequence,
            Round = source.Round,
            Kind = source.Kind,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt,
            RoleActorId = source.RoleActorId,
            Model = source.Model,
            Provider = source.Provider,
            InputSummary = source.InputSummary,
            AvailableToolNames = source.AvailableToolNames.ToList(),
            Output = source.Output,
            ReasoningContent = source.ReasoningContent,
            FinishReason = source.FinishReason,
            Usage = MapUsage(source.Usage),
            Success = source.Success,
            Error = source.Error,
            ToolCallId = source.ToolCallId,
            ToolName = source.ToolName,
            ArgumentsJson = source.ArgumentsJson,
            ResultJson = source.ResultJson,
        };

    private static WorkflowRunTimelineEvent MapTimelineEvent(
        WorkflowExecutionTimelineEvent source,
        IDictionary<string, WorkflowStepRequestEvidence> requestEvidenceById) =>
        new()
        {
            Timestamp = source.TimestampUtcValue?.ToDateTimeOffset() ?? default,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = ResolveRequestParameters(
                source.RequestEvidenceReference,
                source.DataMap,
                requestEvidenceById,
                source.StepId),
        };

    private static Dictionary<string, string> ResolveRequestParameters(
        WorkflowStepRequestEvidenceReference? reference,
        IEnumerable<KeyValuePair<string, string>> legacyParameters,
        IDictionary<string, WorkflowStepRequestEvidence>? requestEvidenceById,
        string expectedStepId)
    {
        if (reference == null || string.IsNullOrWhiteSpace(reference.EvidenceId))
            return legacyParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        if (requestEvidenceById == null ||
            !requestEvidenceById.TryGetValue(reference.EvidenceId, out var evidence))
        {
            throw new InvalidOperationException(
                $"Workflow request evidence '{reference.EvidenceId}' is missing from the report document.");
        }

        var matches = string.Equals(evidence.EvidenceId, reference.EvidenceId, StringComparison.Ordinal) &&
                      string.Equals(evidence.ExecutionId, reference.ExecutionId, StringComparison.Ordinal) &&
                      string.Equals(evidence.SourceEventId, reference.SourceEventId, StringComparison.Ordinal) &&
                      (string.IsNullOrWhiteSpace(expectedStepId) ||
                       string.Equals(evidence.StepId, expectedStepId, StringComparison.Ordinal));
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Workflow request evidence reference '{reference.EvidenceId}' does not match its typed identity.");
        }

        return evidence.ParametersMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

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

    private static WorkflowRunUsageMetrics MapUsage(WorkflowUsageMetricsReadModel? source) =>
        source == null
            ? new WorkflowRunUsageMetrics()
            : new WorkflowRunUsageMetrics
            {
                PromptTokens = source.PromptTokens,
                CompletionTokens = source.CompletionTokens,
                TotalTokens = source.TotalTokens,
                Model = source.Model,
                Cost = source.Cost,
                LatencyMs = source.LatencyMs,
            };
}
