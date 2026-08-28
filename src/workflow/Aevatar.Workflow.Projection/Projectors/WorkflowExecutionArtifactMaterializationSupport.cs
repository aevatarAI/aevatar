using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Projectors;

internal static class WorkflowExecutionArtifactMaterializationSupport
{
    private const string CurrentReportVersion = "3.1";

    private delegate void ObservedPayloadHandler(
        WorkflowRunInsightReportDocument readModel,
        Google.Protobuf.WellKnownTypes.Any payload,
        DateTimeOffset observedAt,
        string sourceEventId);

    private static readonly IReadOnlyDictionary<string, ObservedPayloadHandler> ObservedPayloadHandlers =
        new Dictionary<string, ObservedPayloadHandler>(StringComparer.Ordinal)
        {
            [BuildTypeUrl(WorkflowRunExecutionStartedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowRunExecutionStarted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRunExecutionStartedEvent>(),
                    observedAt),
            [BuildTypeUrl(StepRequestEvent.Descriptor)] = static (readModel, payload, observedAt, sourceEventId) =>
                ApplyStepRequest(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<StepRequestEvent>(),
                    observedAt,
                    sourceEventId),
            [BuildTypeUrl(StepCompletedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyStepCompleted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<StepCompletedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowSuspendedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowSuspended(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowSuspendedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowToolApprovalResumeRejectedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowToolApprovalResumeRejected(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowToolApprovalResumeRejectedEvent>(),
                    observedAt),
            [BuildTypeUrl(WaitingForSignalEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWaitingForSignal(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WaitingForSignalEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowSignalBufferedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowSignalBuffered(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowSignalBufferedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowRoleActorLinkedEvent.Descriptor)] = static (readModel, payload, _, _) =>
                ApplyWorkflowRoleActorLinked(
                    readModel,
                    payload.Unpack<WorkflowRoleActorLinkedEvent>()),
            [BuildTypeUrl(SubWorkflowBindingUpsertedEvent.Descriptor)] = static (readModel, payload, _, _) =>
                ApplySubWorkflowBindingUpserted(
                    readModel,
                    payload.Unpack<SubWorkflowBindingUpsertedEvent>()),
            [BuildTypeUrl(WorkflowRoleReplyRecordedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowRoleReplyRecorded(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRoleReplyRecordedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowRuntimeOperationRecordedEvent.Descriptor)] = static (readModel, payload, _, _) =>
                ApplyWorkflowRuntimeOperationRecorded(
                    readModel,
                    payload.Unpack<WorkflowRuntimeOperationRecordedEvent>()),
            [BuildTypeUrl(WorkflowCompletedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowCompleted(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowCompletedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowStoppedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowStopped(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowStoppedEvent>(),
                    observedAt),
            [BuildTypeUrl(WorkflowRunStoppedEvent.Descriptor)] = static (readModel, payload, observedAt, _) =>
                ApplyWorkflowRunStopped(
                    readModel,
                    payload.TypeUrl ?? string.Empty,
                    payload.Unpack<WorkflowRunStoppedEvent>(),
                    observedAt),
        };

    public static bool TryUnpackRootStateEnvelope(
        EventEnvelope envelope,
        out StateEvent? stateEvent,
        out WorkflowRunState? state)
    {
        if (CommittedStateEventEnvelope.TryUnpackState<WorkflowRunState>(
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

    public static bool ShouldSkip(IProjectionReadModel existing, StateEvent stateEvent)
    {
        if (existing.StateVersion > stateEvent.Version)
            return true;

        return existing.StateVersion == stateEvent.Version &&
               string.Equals(existing.LastEventId, stateEvent.EventId ?? string.Empty, StringComparison.Ordinal);
    }

    public static WorkflowRunInsightReportDocument CreateReportDocument(
        WorkflowExecutionMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        var readModel = new WorkflowRunInsightReportDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            ReportVersion = CurrentReportVersion,
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            // Refactor (iter33/cluster-035-workflow-report-runtime-topology-sideread):
            //   Old pattern: Workflow report 用 IActorRuntime.GetAsync(...).GetChildrenIdsAsync() 读 runtime children 当 topology 事实,违反 runtime-shape-not-fact + side-read
            //   New principle: 删 IWorkflowExecutionTopologyResolver + ActorRuntimeWorkflowExecutionTopologyResolver;topology 从 committed event projection 来(WorkflowRoleActorLinkedEvent + SubWorkflowBindingUpsertedEvent 已 materialize);enum 值 RuntimeSnapshot 改 CommittedProjection;无 proto 改
            TopologySource = WorkflowExecutionTopologySource.CommittedProjection,
            CreatedAt = observedAt,
        };
        ApplyReportBase(readModel, context, state, stateEvent, observedAt);
        return readModel;
    }

    public static void ApplyReportBase(
        WorkflowRunInsightReportDocument readModel,
        WorkflowExecutionMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        readModel.Id = context.RootActorId;
        readModel.RootActorId = context.RootActorId;
        readModel.CommandId = state.LastCommandId ?? string.Empty;
        readModel.WorkflowName = ResolveWorkflowName(state, readModel.WorkflowName);
        readModel.Input = SanitizeAuditText(state.Input);
        readModel.FinalOutput = SanitizeAuditText(state.FinalOutput);
        readModel.FinalError = WorkflowAuditTextSanitizer.SanitizeForStorage(state.FinalError);
        readModel.Success = ResolveSuccess(state.Status);
        readModel.CompletionStatus = ResolveCompletionStatus(state.Status, readModel.CompletionStatus);
        readModel.StateVersion = stateEvent.Version;
        readModel.LastEventId = stateEvent.EventId ?? string.Empty;
        readModel.UpdatedAt = observedAt;

        if (readModel.CreatedAt == default)
            readModel.CreatedAt = observedAt;
        if (readModel.StartedAt == default && string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
            readModel.StartedAt = observedAt;
        // EndedAt records when the run first reached a terminal status. A duplicate delivery or
        // a maintenance republish of the terminal outcome must not move it to its own
        // observation time.
        if (IsTerminalStatus(state.Status) && readModel.EndedAt == default)
            readModel.EndedAt = observedAt;
    }

    public static void ApplyObservedPayloadToReport(
        WorkflowRunInsightReportDocument readModel,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        var payload = stateEvent.EventData;
        if (payload == null)
        {
            RefreshSummary(readModel);
            return;
        }

        if (ObservedPayloadHandlers.TryGetValue(payload.TypeUrl ?? string.Empty, out var handler))
            handler(readModel, payload, observedAt, stateEvent.EventId ?? string.Empty);

        RefreshSummary(readModel);
    }

    private static void ApplyWorkflowRunExecutionStarted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRunExecutionStartedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? readModel.WorkflowName : evt.WorkflowName;
        readModel.Input = SanitizeAuditText(evt.Input);
        if (readModel.StartedAt == default)
            readModel.StartedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.start",
            $"command={readModel.CommandId}",
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static void ApplyStepRequest(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        StepRequestEvent evt,
        DateTimeOffset observedAt,
        string sourceEventId)
    {
        var parameters = WorkflowStepParameterProjectionSource.From(evt);
        var evidenceReference = string.IsNullOrWhiteSpace(evt.ExecutionId)
            ? null
            : GetOrAddRequestEvidence(
                readModel,
                evt.StepId ?? string.Empty,
                evt.ExecutionId,
                sourceEventId,
                parameters);

        var step = GetOrCreateStep(readModel, evt.StepId);
        if (step.Outcome == WorkflowExecutionStepOutcomeReadModel.Failed)
            step.LatestFailedAttempt = SnapshotFailedAttempt(step);
        ResetCurrentAttempt(step);
        step.StepId = evt.StepId ?? string.Empty;
        step.DisplayName = ResolveStepDisplayName(evt.DisplayName, step.StepId);
        step.StepType = evt.StepType ?? string.Empty;
        step.TargetRole = evt.TargetRole ?? string.Empty;
        step.RequestedAt = observedAt;
        step.Outcome = WorkflowExecutionStepOutcomeReadModel.Waiting;
        if (evidenceReference == null)
        {
            // Legacy events have no immutable attempt identity. Preserve their inline shape rather
            // than inventing an execution id or binding history to the mutable latest step.
            ReplaceMap(step.RequestParameters, parameters);
        }
        else
        {
            step.RequestParameters.Clear();
            step.RequestEvidenceReference = evidenceReference.Clone();
        }

        AddTimeline(
            readModel.Timeline,
            observedAt,
            "step.request",
            $"{evt.StepId} ({evt.StepType})",
            readModel.RootActorId,
            evt.StepId,
            evt.StepType,
            eventType,
            evidenceReference == null ? parameters : null,
            evidenceReference);
    }

    private static void ApplyStepCompleted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        StepCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        var step = GetOrCreateStep(readModel, evt.StepId);
        step.StepId = evt.StepId ?? string.Empty;
        step.CompletedAt = observedAt;
        step.Success = evt.Success;
        step.Outcome = ResolveStepOutcome(evt);
        step.LatestFailedAttempt = null;
        var isFailure = step.Outcome == WorkflowExecutionStepOutcomeReadModel.Failed;
        step.Error = isFailure
            ? WorkflowAuditTextSanitizer.SanitizeForStorage(evt.Error)
            : SanitizeAuditText(evt.Error);
        var failureOutputTruncated = false;
        step.FailureOutput = isFailure
            ? SanitizeAuditTextForStorage(
                evt.Output,
                WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes,
                out failureOutputTruncated)
            : string.Empty;
        step.FailureOutputTruncated = isFailure && failureOutputTruncated;
        step.OutputPreview = isFailure
            ? SanitizeAuditTextForDisplay(step.FailureOutput, 240)
            : SanitizeAuditTextForDisplay(evt.Output, 240);
        step.FailureOutcome = isFailure
            ? evt.FailureOutcome
            : WorkflowStepFailureOutcome.Unspecified;
        step.RecoveryFailureKind = isFailure
            ? evt.RecoveryFailureKind
            : WorkflowRecoveryFailureKind.Unspecified;
        step.RetryDisposition = isFailure
            ? evt.RetryDisposition
            : WorkflowStepRetryDisposition.Unspecified;
        step.FileItemResults = SanitizeFileItemResults(evt.FileItemResults);
        step.VoteAgreementDecision = SanitizeVoteAgreementDecision(evt.VoteAgreementDecision);
        step.WorkerId = evt.WorkerId ?? string.Empty;
        step.NextStepId = evt.NextStepId ?? string.Empty;
        step.BranchKey = evt.BranchKey ?? string.Empty;
        step.AssignedVariable = evt.AssignedVariable ?? string.Empty;
        step.AssignedValue = SanitizeAuditValue(evt.AssignedVariable, evt.AssignedValue);
        step.Usage = ToReadModelUsage(evt.Usage);
        ReplaceMap(step.CompletionAnnotations, evt.Annotations);
        AddTimeline(
            readModel.Timeline,
            observedAt,
            isFailure ? "step.failed" : "step.completed",
            isFailure
                ? ResolveFailureTimelineMessage(step.Error, $"{evt.StepId} (failed)")
                : $"{evt.StepId} ({(evt.Success ? "success" : "completed")})",
            evt.WorkerId,
            evt.StepId,
            step.StepType,
            eventType,
            BuildStepCompletionTimelineData(evt, isFailure, step.Error));
    }

    private static void ApplyWorkflowSuspended(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowSuspendedEvent evt,
        DateTimeOffset observedAt)
    {
        var step = GetOrCreateStep(readModel, evt.StepId);
        step.SuspensionType = evt.SuspensionType ?? string.Empty;
        step.SuspensionPrompt = SanitizeAuditText(evt.Prompt);
        step.SuspensionContent = evt.Secure
            ? string.Empty
            : SanitizeAuditText(evt.Content);
        step.SuspensionTimeoutSeconds = evt.TimeoutSeconds == 0 ? null : evt.TimeoutSeconds;
        step.RequestedVariableName = evt.VariableName ?? string.Empty;
        step.ToolApprovalValue = evt.ToolApproval == null
            ? null
            : new WorkflowToolApprovalReadModel
            {
                ExecutionId = evt.ToolApproval.ExecutionId,
                ToolName = evt.ToolApproval.ToolName,
                ToolCallId = evt.ToolApproval.ToolCallId,
                ApprovalRequestId = evt.ToolApproval.ApprovalRequestId,
            };
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
        // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
        //   Old pattern: WorkflowSuspendedEvent.Metadata fallback for variable/secure/redacted_output reserved keys.
        //   New principle: typed suspension fields are the single source; Metadata is open extension data only.
        var timelineMetadata = BuildWorkflowSuspendedTimelineMetadata(evt);
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.suspended",
            $"{evt.StepId} ({evt.SuspensionType})",
            readModel.RootActorId,
            evt.StepId,
            step.StepType,
            eventType,
            timelineMetadata);
    }

    private static Dictionary<string, string> BuildWorkflowSuspendedTimelineMetadata(WorkflowSuspendedEvent evt)
    {
        var metadata = FilterOpenExtensionMetadata(evt.Metadata);
        var variableName = !string.IsNullOrWhiteSpace(evt.VariableName) ? evt.VariableName : string.Empty;
        var secure = evt.Secure;
        var redactedOutput = !string.IsNullOrWhiteSpace(evt.RedactedOutput) ? evt.RedactedOutput : string.Empty;

        if (!string.IsNullOrWhiteSpace(variableName))
            metadata["variable"] = variableName;
        if (secure)
            metadata["secure"] = "true";
        if (!string.IsNullOrWhiteSpace(redactedOutput))
            metadata["redacted_output"] = redactedOutput;

        return metadata;
    }

    private static void ApplyWorkflowToolApprovalResumeRejected(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowToolApprovalResumeRejectedEvent evt,
        DateTimeOffset observedAt)
    {
        var submitted = evt.SubmittedApproval;
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = evt.Reason.ToString(),
            ["execution_id"] = submitted?.ExecutionId ?? string.Empty,
            ["tool_call_id"] = submitted?.ToolCallId ?? string.Empty,
            ["approval_request_id"] = submitted?.ApprovalRequestId ?? string.Empty,
        };
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "tool_approval.resume_rejected",
            "Tool approval resume did not match the actor-owned pending approval.",
            readModel.RootActorId,
            evt.StepId,
            "tool_call",
            eventType,
            data);
    }

    private static Dictionary<string, string> FilterOpenExtensionMetadata(IDictionary<string, string> metadata)
    {
        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (key is "variable" or "secure" or "input_mode" or "redacted_output")
                continue;

            filtered[key] = value;
        }

        return filtered;
    }

    private static void ApplyWaitingForSignal(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WaitingForSignalEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "signal.waiting",
            SanitizeAuditText(evt.SignalName),
            readModel.RootActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
                ["timeout_ms"] = evt.TimeoutMs.ToString(),
            });
    }

    private static void ApplyWorkflowSignalBuffered(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowSignalBufferedEvent evt,
        DateTimeOffset observedAt)
    {
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "signal.buffered",
            SanitizeAuditText(evt.SignalName),
            readModel.RootActorId,
            evt.StepId,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signal_name"] = evt.SignalName ?? string.Empty,
            });
    }

    private static void ApplyWorkflowRoleActorLinked(
        WorkflowRunInsightReportDocument readModel,
        WorkflowRoleActorLinkedEvent evt)
    {
        // Topology is materialized from committed link events, not runtime children.
        UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
    }

    private static void ApplySubWorkflowBindingUpserted(
        WorkflowRunInsightReportDocument readModel,
        SubWorkflowBindingUpsertedEvent evt)
    {
        // Topology is materialized from committed link events, not runtime children.
        UpsertTopology(readModel.Topology, readModel.RootActorId, evt.ChildActorId);
    }

    private static void ApplyWorkflowRoleReplyRecorded(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRoleReplyRecordedEvent evt,
        DateTimeOffset observedAt)
    {
        var roleId = string.IsNullOrWhiteSpace(evt.RoleId) ? evt.RoleActorId : evt.RoleId;
        var content = SanitizeAuditText(evt.Content);
        readModel.RoleReplies.Add(new WorkflowExecutionRoleReply
        {
            Timestamp = observedAt,
            RoleId = roleId,
            SessionId = evt.SessionId ?? string.Empty,
            Content = content,
            ContentLength = content.Length,
        });
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "role.reply",
            roleId,
            evt.RoleActorId,
            null,
            null,
            eventType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            });

        foreach (var toolCall in evt.ToolCalls)
        {
            AddTimeline(
                readModel.Timeline,
                observedAt,
                "tool.call",
                toolCall.ToolName ?? string.Empty,
                evt.RoleActorId,
                null,
                null,
                eventType,
                BuildToolCallTimelineData(toolCall));

            EnrichToolOperation(readModel.Operations, evt.SessionId, toolCall);
        }
    }

    private static void ApplyWorkflowRuntimeOperationRecorded(
        WorkflowRunInsightReportDocument readModel,
        WorkflowRuntimeOperationRecordedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId) ||
            string.IsNullOrWhiteSpace(evt.OperationId) ||
            evt.Kind == WorkflowRuntimeOperationKind.Unspecified ||
            evt.Phase == WorkflowRuntimeOperationPhase.Unspecified)
        {
            return;
        }

        var operation = readModel.Operations.FirstOrDefault(candidate =>
            candidate.Kind == evt.Kind &&
            string.Equals(candidate.SessionId, evt.SessionId, StringComparison.Ordinal) &&
            string.Equals(candidate.OperationId, evt.OperationId, StringComparison.Ordinal));
        if (operation == null)
        {
            operation = new WorkflowRuntimeOperationReadModel
            {
                SessionId = evt.SessionId,
                OperationId = evt.OperationId,
                Kind = evt.Kind,
                ProgressSequence = evt.ProgressSequence,
            };
            readModel.Operations.Add(operation);
        }
        var eventTime = evt.EventTime?.ToDateTimeOffset();
        if (evt.Phase == WorkflowRuntimeOperationPhase.Started)
        {
            if (!ShouldApplyOperationPhase(evt.ProgressSequence, operation.StartedProgressSequence))
                return;

            UpdateOperationAnchorSequence(operation, evt.ProgressSequence);
            operation.StartedProgressSequence = evt.ProgressSequence;
            operation.Round = evt.Round;
            operation.RoleActorId = FirstNonEmpty(evt.RoleActorId, operation.RoleActorId);
            operation.Model = FirstNonEmpty(evt.Model, operation.Model);
            operation.Provider = FirstNonEmpty(evt.Provider, operation.Provider);
            operation.InputSummary = SanitizeAuditTextForDisplay(evt.InputSummary, 2000);
            operation.AvailableToolNames.Clear();
            operation.AvailableToolNames.Add(
                evt.AvailableToolNames
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static name => name, StringComparer.Ordinal));
            operation.ToolCatalogPolicyVersion = evt.ToolCatalogPolicyVersion ?? string.Empty;
            operation.ToolCatalogToolCount = Math.Max(0, evt.ToolCatalogProof?.ToolCount ?? 0);
            operation.ToolCatalogSchemaBytes = Math.Max(0, evt.ToolCatalogProof?.SchemaBytes ?? 0);
            operation.ToolCatalogDigest = evt.ToolCatalogProof?.CatalogDigest ?? string.Empty;
            operation.ToolCallId = FirstNonEmpty(evt.ToolCallId, operation.ToolCallId);
            operation.ToolName = FirstNonEmpty(evt.ToolName, operation.ToolName);
            if (evt.ProgressSequence > 0 && eventTime.HasValue)
            {
                operation.StartedAt = eventTime;
            }
            else if (eventTime.HasValue &&
                     (!operation.StartedAt.HasValue || eventTime.Value < operation.StartedAt.Value))
            {
                operation.StartedAt = eventTime;
            }
            return;
        }

        if (!ShouldApplyOperationPhase(evt.ProgressSequence, operation.CompletedProgressSequence))
            return;

        UpdateOperationAnchorSequence(operation, evt.ProgressSequence);
        operation.CompletedProgressSequence = evt.ProgressSequence;
        operation.Round = evt.Round;
        operation.RoleActorId = FirstNonEmpty(evt.RoleActorId, operation.RoleActorId);
        operation.Model = FirstNonEmpty(evt.Model, operation.Model);
        operation.ToolCallId = FirstNonEmpty(evt.ToolCallId, operation.ToolCallId);
        operation.ToolName = FirstNonEmpty(evt.ToolName, operation.ToolName);
        if (evt.ProgressSequence > 0 && eventTime.HasValue)
        {
            operation.CompletedAt = eventTime;
        }
        else if (eventTime.HasValue &&
                 (!operation.CompletedAt.HasValue || eventTime.Value > operation.CompletedAt.Value))
        {
            operation.CompletedAt = eventTime;
        }
        operation.Output = SanitizeAuditText(evt.Output);
        operation.ReasoningContent = SanitizeAuditText(evt.ReasoningContent);
        operation.FinishReason = SanitizeAuditTextForDisplay(evt.FinishReason, 128);
        operation.Success = evt.Success;
        operation.Error = SanitizeAuditText(evt.Error);
        operation.ArgumentsJson = SanitizeAuditText(evt.ArgumentsJson);
        operation.ResultJson = SanitizeAuditText(evt.ResultJson);
        operation.Usage = evt.Usage == null
            ? new WorkflowUsageMetricsReadModel()
            : ToReadModelUsage(evt.Usage);
    }

    private static bool ShouldApplyOperationPhase(long incomingSequence, long currentSequence) =>
        currentSequence <= 0 || incomingSequence > currentSequence;

    private static void UpdateOperationAnchorSequence(
        WorkflowRuntimeOperationReadModel operation,
        long incomingSequence)
    {
        if (operation.ProgressSequence <= 0 ||
            incomingSequence > 0 && incomingSequence < operation.ProgressSequence)
        {
            operation.ProgressSequence = incomingSequence;
        }
    }

    private static void EnrichToolOperation(
        IList<WorkflowRuntimeOperationReadModel> operations,
        string? sessionId,
        WorkflowRoleReplyToolCall toolCall)
    {
        var callId = toolCall.CallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
            return;

        var operation = operations.FirstOrDefault(candidate =>
            candidate.Kind == WorkflowRuntimeOperationKind.Tool &&
            string.Equals(candidate.SessionId, sessionId ?? string.Empty, StringComparison.Ordinal) &&
            (string.Equals(candidate.ToolCallId, callId, StringComparison.Ordinal) ||
             string.Equals(candidate.OperationId, callId, StringComparison.Ordinal)));
        if (operation == null)
            return;

        operation.ToolName = FirstNonEmpty(toolCall.ToolName, operation.ToolName);
        operation.ResultJson = FirstNonEmpty(
            operation.ResultJson,
            SanitizeAuditText(toolCall.ResultJson));
        operation.Error = FirstNonEmpty(
            operation.Error,
            SanitizeAuditText(toolCall.Error));
    }

    private static string FirstNonEmpty(string? candidate, string? fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback ?? string.Empty : candidate;

    private static Dictionary<string, string> BuildToolCallTimelineData(WorkflowRoleReplyToolCall toolCall)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["call_id"] = SanitizeAuditText(toolCall.CallId),
            ["success"] = toolCall.Success ? "true" : "false",
        };

        if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
            data["arguments_json"] = SanitizeAuditText(toolCall.ArgumentsJson);
        if (!string.IsNullOrEmpty(toolCall.ResultJson))
            data["result_json"] = SanitizeAuditText(toolCall.ResultJson);
        if (!string.IsNullOrEmpty(toolCall.Error))
            data["error"] = SanitizeAuditText(toolCall.Error);

        return data;
    }

    private static Dictionary<string, string> BuildStepCompletionTimelineData(
        StepCompletedEvent evt,
        bool isFailure,
        string sanitizedError)
    {
        var data = evt.Annotations.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        if (isFailure && !string.IsNullOrWhiteSpace(sanitizedError))
            data["error"] = sanitizedError;
        return data;
    }

    private static IReadOnlyDictionary<string, string>? BuildFailureTimelineData(string sanitizedError) =>
        string.IsNullOrWhiteSpace(sanitizedError)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["error"] = sanitizedError,
            };

    private static string ResolveFailureTimelineMessage(string sanitizedError, string fallback) =>
        string.IsNullOrWhiteSpace(sanitizedError) ? fallback : sanitizedError;

    private static void ApplyWorkflowCompleted(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = evt.Success
            ? WorkflowExecutionCompletionStatus.Completed
            : WorkflowExecutionCompletionStatus.Failed;
        readModel.Success = evt.Success;
        readModel.FinalOutput = SanitizeAuditText(evt.Output);
        readModel.FinalError = evt.Success
            ? SanitizeAuditText(evt.Error)
            : WorkflowAuditTextSanitizer.SanitizeForStorage(evt.Error);
        var terminalStage = evt.Success ? "workflow.completed" : "workflow.failed";
        // A run reaches its terminal outcome exactly once; a maintenance republish or a
        // redelivery of the same outcome must not append a second terminal timeline entry
        // or move EndedAt to the observation time of the duplicate.
        if (readModel.Timeline.Any(entry => entry.Stage == terminalStage))
            return;

        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            terminalStage,
            evt.Success
                ? "completed"
                : ResolveFailureTimelineMessage(readModel.FinalError, "failed"),
            readModel.RootActorId,
            null,
            null,
            eventType,
            evt.Success ? null : BuildFailureTimelineData(readModel.FinalError));
    }

    private static void ApplyWorkflowStopped(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;
        readModel.Success = false;
        readModel.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            readModel.FinalError = SanitizeAuditText(evt.Reason);
        // Same terminal-outcome idempotence as ApplyWorkflowCompleted.
        if (readModel.Timeline.Any(entry => entry.Stage == "workflow.stopped"))
            return;

        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.stopped",
            SanitizeAuditText(evt.Reason ?? "stopped"),
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static void ApplyWorkflowRunStopped(
        WorkflowRunInsightReportDocument readModel,
        string eventType,
        WorkflowRunStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        readModel.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;
        readModel.Success = false;
        readModel.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            readModel.FinalError = SanitizeAuditText(evt.Reason);
        // Same terminal-outcome idempotence as ApplyWorkflowCompleted.
        if (readModel.Timeline.Any(entry => entry.Stage == "workflow.stopped"))
            return;

        readModel.EndedAt = observedAt;
        AddTimeline(
            readModel.Timeline,
            observedAt,
            "workflow.stopped",
            SanitizeAuditText(evt.Reason ?? "stopped"),
            readModel.RootActorId,
            null,
            null,
            eventType,
            null);
    }

    private static WorkflowExecutionStepTrace GetOrCreateStep(
        WorkflowRunInsightReportDocument readModel,
        string? stepId)
    {
        var normalizedStepId = stepId ?? string.Empty;
        if (TryGetIndexedStep(readModel, normalizedStepId, out var indexed))
            return indexed;

        if (readModel.StepIndexById.Count == 0 && readModel.Steps.Count > 0)
            RebuildStepIndex(readModel);

        if (TryGetIndexedStep(readModel, normalizedStepId, out indexed))
            return indexed;

        var existing = new WorkflowExecutionStepTrace
        {
            StepId = normalizedStepId,
        };
        readModel.StepIndexById[normalizedStepId] = readModel.Steps.Count;
        readModel.Steps.Add(existing);
        return existing;
    }

    internal static bool TryGetIndexedStep(
        WorkflowRunInsightReportDocument readModel,
        string? stepId,
        out WorkflowExecutionStepTrace step)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        var normalizedStepId = stepId ?? string.Empty;
        if (readModel.StepIndexById.TryGetValue(normalizedStepId, out var index) &&
            index >= 0 &&
            index < readModel.Steps.Count &&
            string.Equals(readModel.Steps[index].StepId, normalizedStepId, StringComparison.Ordinal))
        {
            step = readModel.Steps[index];
            return true;
        }

        step = null!;
        return false;
    }

    private static void RebuildStepIndex(WorkflowRunInsightReportDocument readModel)
    {
        readModel.StepIndexById.Clear();
        for (var index = 0; index < readModel.Steps.Count; index++)
        {
            var stepId = readModel.Steps[index].StepId ?? string.Empty;
            if (!readModel.StepIndexById.ContainsKey(stepId))
                readModel.StepIndexById[stepId] = index;
        }
    }

    private static WorkflowStepRequestEvidenceReference GetOrAddRequestEvidence(
        WorkflowRunInsightReportDocument readModel,
        string stepId,
        string executionId,
        string sourceEventId,
        IEnumerable<KeyValuePair<string, string>> sourceParameters)
    {
        var normalizedStepId = stepId?.Trim() ?? string.Empty;
        var normalizedExecutionId = executionId?.Trim() ?? string.Empty;
        var normalizedSourceEventId = sourceEventId?.Trim() ?? string.Empty;
        if (normalizedStepId.Length == 0 || normalizedExecutionId.Length == 0)
        {
            throw new InvalidOperationException(
                "Workflow request evidence requires non-empty step and execution identities.");
        }

        var source = sourceParameters.ToArray();
        var retained = new Dictionary<string, string>(StringComparer.Ordinal);
        ReplaceMap(retained, source);
        var evidenceId = BuildRequestEvidenceId(normalizedStepId, normalizedExecutionId);
        if (readModel.RequestEvidenceById.TryGetValue(evidenceId, out var existing))
        {
            EnsureRequestEvidenceMatches(
                existing,
                evidenceId,
                normalizedStepId,
                normalizedExecutionId,
                retained);
            return ToRequestEvidenceReference(existing);
        }

        var evidence = new WorkflowStepRequestEvidence
        {
            EvidenceId = evidenceId,
            StepId = normalizedStepId,
            ExecutionId = normalizedExecutionId,
            SourceEventId = normalizedSourceEventId,
            SourceParameterUtf8Bytes = CalculateParameterUtf8Bytes(source),
            RetainedParameterUtf8Bytes = CalculateParameterUtf8Bytes(retained),
            RetainedParameterSha256 = ComputeParameterSha256(retained),
        };
        evidence.ParametersMap.Add(retained);
        readModel.RequestEvidenceById.Add(evidenceId, evidence);
        return ToRequestEvidenceReference(evidence);
    }

    private static void EnsureRequestEvidenceMatches(
        WorkflowStepRequestEvidence evidence,
        string evidenceId,
        string stepId,
        string executionId,
        IReadOnlyDictionary<string, string> retainedParameters)
    {
        // A pending dispatch can be committed again with a new source event id. The execution
        // identity and request parameters are immutable; SourceEventId remains the provenance of
        // the first committed request evidence and is intentionally not part of this comparison.
        var identityMatches = string.Equals(evidence.EvidenceId, evidenceId, StringComparison.Ordinal) &&
                              string.Equals(evidence.StepId, stepId, StringComparison.Ordinal) &&
                              string.Equals(evidence.ExecutionId, executionId, StringComparison.Ordinal);
        var parametersMatch = evidence.ParametersMap.Count == retainedParameters.Count &&
                              retainedParameters.All(parameter =>
                                  evidence.ParametersMap.TryGetValue(parameter.Key, out var value) &&
                                  string.Equals(value, parameter.Value, StringComparison.Ordinal));
        if (identityMatches && parametersMatch)
            return;

        throw new InvalidOperationException(
            $"Workflow request evidence '{evidenceId}' is already bound to different immutable content.");
    }

    private static WorkflowStepRequestEvidenceReference ToRequestEvidenceReference(
        WorkflowStepRequestEvidence evidence) =>
        new()
        {
            EvidenceId = evidence.EvidenceId,
            ExecutionId = evidence.ExecutionId,
            SourceEventId = evidence.SourceEventId,
        };

    private static string BuildRequestEvidenceId(string stepId, string executionId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, "workflow-step-request-evidence.v1");
        AppendHashField(hash, stepId);
        AppendHashField(hash, executionId);
        return "request-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeParameterSha256(IReadOnlyDictionary<string, string> parameters)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var parameter in parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            AppendHashField(hash, parameter.Key);
            AppendHashField(hash, parameter.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static long CalculateParameterUtf8Bytes(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        long result = 0;
        foreach (var parameter in parameters)
        {
            result = checked(result + Encoding.UTF8.GetByteCount(parameter.Key));
            result = checked(result + Encoding.UTF8.GetByteCount(parameter.Value));
        }

        return result;
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string BuildTypeUrl(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return "type.googleapis.com/" + descriptor.FullName;
    }

    private static void UpsertTopology(
        IList<WorkflowExecutionTopologyEdge> topology,
        string parent,
        string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
            return;

        if (topology.Any(x =>
                string.Equals(x.Parent, parent, StringComparison.Ordinal) &&
                string.Equals(x.Child, child, StringComparison.Ordinal)))
        {
            return;
        }

        topology.Add(new WorkflowExecutionTopologyEdge(parent, child));
    }

    private static void AddTimeline(
        IList<WorkflowExecutionTimelineEvent> timeline,
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        IEnumerable<KeyValuePair<string, string>>? data,
        WorkflowStepRequestEvidenceReference? requestEvidenceReference = null)
    {
        timeline.Add(new WorkflowExecutionTimelineEvent
        {
            Timestamp = timestamp,
            Stage = stage,
            Message = message,
            AgentId = agentId ?? string.Empty,
            StepId = stepId ?? string.Empty,
            StepType = stepType ?? string.Empty,
            EventType = eventType ?? string.Empty,
            Data = SanitizeAuditMap(data),
            RequestEvidenceReference = requestEvidenceReference?.Clone(),
        });
    }

    private static WorkflowExecutionTimelineEvent CloneTimelineEvent(WorkflowExecutionTimelineEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionTimelineEvent
        {
            Timestamp = source.Timestamp,
            Stage = source.Stage,
            Message = source.Message,
            AgentId = source.AgentId,
            StepId = source.StepId,
            StepType = source.StepType,
            EventType = source.EventType,
            Data = source.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            RequestEvidenceReference = source.RequestEvidenceReference?.Clone(),
        };
    }

    private static WorkflowExecutionStepTrace CloneStepTrace(WorkflowExecutionStepTrace source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionStepTrace
        {
            StepId = source.StepId,
            DisplayName = source.DisplayName,
            StepType = source.StepType,
            TargetRole = source.TargetRole,
            RequestedAt = source.RequestedAt,
            CompletedAt = source.CompletedAt,
            Success = source.Success,
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
            LatestFailedAttempt = source.LatestFailedAttempt?.Clone(),
            RequestParameters = source.RequestParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            RequestEvidenceReference = source.RequestEvidenceReference?.Clone(),
            CompletionAnnotations = source.CompletionAnnotations.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionContent = source.SuspensionContent,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
            RequestedVariableName = source.RequestedVariableName,
            ToolApprovalValue = source.ToolApprovalValue?.Clone(),
            Usage = CloneUsage(source.Usage),
            Outcome = source.Outcome,
        };
    }

    private static WorkflowExecutionFailedStepAttemptReadModel SnapshotFailedAttempt(
        WorkflowExecutionStepTrace source)
    {
        return new WorkflowExecutionFailedStepAttemptReadModel
        {
            DisplayName = source.DisplayName,
            StepType = source.StepType,
            TargetRole = source.TargetRole,
            RequestedAt = source.RequestedAt,
            CompletedAt = source.CompletedAt,
            Success = source.Success,
            WorkerId = source.WorkerId,
            OutputPreview = source.OutputPreview,
            Error = source.Error,
            RequestParameters = source.RequestParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            RequestEvidenceReference = source.RequestEvidenceReference?.Clone(),
            CompletionAnnotations = source.CompletionAnnotations.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            NextStepId = source.NextStepId,
            BranchKey = source.BranchKey,
            AssignedVariable = source.AssignedVariable,
            AssignedValue = source.AssignedValue,
            Usage = CloneUsage(source.Usage),
            FailureOutput = source.FailureOutput,
            FailureOutputTruncated = source.FailureOutputTruncated,
            FailureOutcome = source.FailureOutcome,
            RecoveryFailureKind = source.RecoveryFailureKind,
            RetryDisposition = source.RetryDisposition,
            FileItemResults = source.FileItemResults?.Clone(),
            VoteAgreementDecision = source.VoteAgreementDecision?.Clone(),
            SuspensionType = source.SuspensionType,
            SuspensionPrompt = source.SuspensionPrompt,
            SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
            RequestedVariableName = source.RequestedVariableName,
            SuspensionContent = source.SuspensionContent,
            ToolApprovalValue = source.ToolApprovalValue?.Clone(),
        };
    }

    private static void ResetCurrentAttempt(WorkflowExecutionStepTrace step)
    {
        step.RequestParameters.Clear();
        step.RequestEvidenceReference = null;
        step.CompletedAt = null;
        step.Success = null;
        step.WorkerId = string.Empty;
        step.OutputPreview = string.Empty;
        step.Error = string.Empty;
        step.FailureOutput = string.Empty;
        step.FailureOutputTruncated = false;
        step.FailureOutcome = WorkflowStepFailureOutcome.Unspecified;
        step.RecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        step.RetryDisposition = WorkflowStepRetryDisposition.Unspecified;
        step.FileItemResults = null;
        step.VoteAgreementDecision = null;
        step.CompletionAnnotations.Clear();
        step.NextStepId = string.Empty;
        step.BranchKey = string.Empty;
        step.AssignedVariable = string.Empty;
        step.AssignedValue = string.Empty;
        step.SuspensionType = string.Empty;
        step.SuspensionPrompt = string.Empty;
        step.SuspensionContent = string.Empty;
        step.SuspensionTimeoutSeconds = null;
        step.RequestedVariableName = string.Empty;
        step.ToolApprovalValue = null;
        step.Usage = new WorkflowUsageMetricsReadModel();
        step.Outcome = WorkflowExecutionStepOutcomeReadModel.Unspecified;
    }

    private static string ResolveStepDisplayName(string? displayName, string stepId)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? stepId : normalized;
    }

    private static WorkflowExecutionStepOutcomeReadModel ResolveStepOutcome(StepCompletedEvent evt)
    {
        if (evt.Outcome == WorkflowStepCompletionOutcome.Skipped || HasSkippedAnnotation(evt.Annotations))
            return WorkflowExecutionStepOutcomeReadModel.Skipped;
        if (evt.Outcome == WorkflowStepCompletionOutcome.Succeeded)
            return WorkflowExecutionStepOutcomeReadModel.Succeeded;
        if (evt.Outcome == WorkflowStepCompletionOutcome.Failed)
            return WorkflowExecutionStepOutcomeReadModel.Failed;

        return evt.Success
            ? WorkflowExecutionStepOutcomeReadModel.Succeeded
            : WorkflowExecutionStepOutcomeReadModel.Failed;
    }

    private static bool HasSkippedAnnotation(IDictionary<string, string> annotations) =>
        annotations.Any(static item =>
            item.Key.EndsWith(".skipped", StringComparison.Ordinal) &&
            string.Equals(item.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static void RefreshSummary(WorkflowRunInsightReportDocument readModel)
    {
        var summary = readModel.Summary;
        summary.TotalSteps = readModel.Steps.Count;
        summary.RequestedSteps = readModel.Steps.Count(x => x.RequestedAt.HasValue);
        summary.CompletedSteps = readModel.Steps.Count(x => x.CompletedAt.HasValue);
        summary.RoleReplyCount = readModel.RoleReplies.Count;
        summary.StepTypeCounts.Clear();
        foreach (var group in readModel.Steps
                     .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
                     .GroupBy(x => x.StepType, StringComparer.OrdinalIgnoreCase))
        {
            summary.StepTypeCounts[group.Key] = group.Count();
        }

        readModel.Usage = BuildRunUsage(readModel.Steps);
    }

    private static WorkflowUsageMetricsReadModel ToReadModelUsage(WorkflowUsageMetrics? usage) =>
        usage == null
            ? new WorkflowUsageMetricsReadModel()
            : new WorkflowUsageMetricsReadModel
            {
                PromptTokens = Math.Max(0, usage.PromptTokens),
                CompletionTokens = Math.Max(0, usage.CompletionTokens),
                TotalTokens = Math.Max(0, usage.TotalTokens),
                Model = usage.Model ?? string.Empty,
                Cost = Math.Max(0, usage.Cost),
                LatencyMs = Math.Max(0, usage.LatencyMs),
            };

    private static WorkflowUsageMetricsReadModel CloneUsage(WorkflowUsageMetricsReadModel? usage) =>
        usage == null
            ? new WorkflowUsageMetricsReadModel()
            : new WorkflowUsageMetricsReadModel
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = usage.Model ?? string.Empty,
                Cost = usage.Cost,
                LatencyMs = usage.LatencyMs,
            };

    private static WorkflowUsageMetricsReadModel BuildRunUsage(IEnumerable<WorkflowExecutionStepTrace> steps)
    {
        var result = new WorkflowUsageMetricsReadModel();
        foreach (var step in steps)
        {
            var usage = step.Usage;
            result.PromptTokens += Math.Max(0, usage.PromptTokens);
            result.CompletionTokens += Math.Max(0, usage.CompletionTokens);
            result.TotalTokens += Math.Max(0, usage.TotalTokens);
            if (!string.IsNullOrWhiteSpace(usage.Model))
                result.Model = usage.Model;
            result.Cost += Math.Max(0, usage.Cost);
            result.LatencyMs += Math.Max(0, usage.LatencyMs);
        }

        return result;
    }

    private static string ResolveWorkflowName(
        WorkflowRunState state,
        string? existingWorkflowName)
    {
        if (!string.IsNullOrWhiteSpace(state.WorkflowName))
            return state.WorkflowName;

        return existingWorkflowName ?? string.Empty;
    }

    private static WorkflowExecutionCompletionStatus ResolveCompletionStatus(
        string? status,
        WorkflowExecutionCompletionStatus existing)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "completed" => WorkflowExecutionCompletionStatus.Completed,
            "timed_out" => WorkflowExecutionCompletionStatus.TimedOut,
            "failed" => WorkflowExecutionCompletionStatus.Failed,
            "stopped" => WorkflowExecutionCompletionStatus.Stopped,
            "running" => existing == WorkflowExecutionCompletionStatus.WaitingForSignal
                ? WorkflowExecutionCompletionStatus.WaitingForSignal
                : WorkflowExecutionCompletionStatus.Running,
            _ => existing == default ? WorkflowExecutionCompletionStatus.Unknown : existing,
        };
    }

    private static bool? ResolveSuccess(string? status) =>
        (status ?? string.Empty).Trim() switch
        {
            "completed" => true,
            "timed_out" => false,
            "failed" => false,
            "stopped" => false,
            _ => null,
        };

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "timed_out", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static void ReplaceMap(
        IDictionary<string, string> target,
        IEnumerable<KeyValuePair<string, string>> source)
    {
        target.Clear();
        foreach (var (key, value) in source)
            target[SanitizeAuditText(key)] = SanitizeAuditValue(key, value);
    }

    private static string SanitizeAuditText(string? value) =>
        WorkflowAuditTextSanitizer.Sanitize(value);

    private static string SanitizeAuditValue(string? key, string? value) =>
        WorkflowAuditTextSanitizer.SanitizeValue(key, value);

    private static string SanitizeAuditTextForDisplay(string? value, int maxLen) =>
        WorkflowAuditTextSanitizer.SanitizeForDisplay(value, maxLen);

    private static string SanitizeAuditTextForStorage(
        string? value,
        int maxUtf8Bytes,
        out bool truncated) =>
        WorkflowAuditTextSanitizer.SanitizeForStorage(value, maxUtf8Bytes, out truncated);

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
        var output = SanitizeAuditTextForStorage(
            item.Output,
            WorkflowFileItemResultProjectionContract.MaxEvidenceUtf8Bytes,
            out var outputTruncated);
        var error = SanitizeAuditTextForStorage(
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
                FileId = SanitizeAuditText(source.FileId),
                ArtifactId = SanitizeAuditText(source.ArtifactId),
                SourceKind = source.SourceKind,
                SourceMessageId = SanitizeAuditText(source.SourceMessageId),
                SourceResourceKey = SanitizeAuditText(source.SourceResourceKey),
                FileName = SanitizeAuditText(source.FileName),
                MediaType = SanitizeAuditText(source.MediaType),
                SizeBytes = source.SizeBytes,
                Sha256 = SanitizeAuditText(source.Sha256),
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = SanitizeAuditText(source.OwnerRunId),
                OwnerScopeId = SanitizeAuditText(source.OwnerScopeId),
            };

    private static VoteAgreementDecision? SanitizeVoteAgreementDecision(VoteAgreementDecision? source)
    {
        if (source == null)
            return null;

        var output = SanitizeAuditTextForStorage(
            source.Output,
            WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes,
            out var outputTruncated);
        var reason = SanitizeAuditTextForStorage(
            source.Reason,
            WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes,
            out var reasonTruncated);
        var sanitized = new VoteAgreementDecision
        {
            Kind = source.Kind,
            BranchKey = SanitizeAuditText(source.BranchKey),
            WinnerCandidateId = SanitizeAuditText(source.WinnerCandidateId),
            Output = output,
            Reason = reason,
            OutputTruncated = source.OutputTruncated || outputTruncated,
            ReasonTruncated = source.ReasonTruncated || reasonTruncated,
        };
        foreach (var (label, count) in source.LabelCounts)
            sanitized.LabelCounts[SanitizeAuditText(label)] = count;
        return sanitized;
    }

    private static Dictionary<string, string> SanitizeAuditMap(IEnumerable<KeyValuePair<string, string>>? data) =>
        WorkflowAuditTextSanitizer.SanitizeMap(data);
}
