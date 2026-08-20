using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Observability;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using YamlDotNet.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionCurrentStateProjector
    : MappedCurrentStateProjectionMaterializer<
        WorkflowExecutionMaterializationContext,
        WorkflowRunState,
        WorkflowExecutionCurrentStateDocument>
{
    private const string FailedStatus = "failed";
    private const string CompletedStatus = "completed";
    private const string StoppedStatus = "stopped";

    private readonly WorkflowRunForkSeedReadModelMapper _forkSeedMapper = new();
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override WorkflowExecutionCurrentStateDocument? Map(
        MappedCurrentStateProjectionInput<WorkflowExecutionMaterializationContext, WorkflowRunState> input)
    {
        var context = input.Context;
        var publisherActorId = input.Envelope.Route?.PublisherActorId ?? string.Empty;
        // Refactor (issue1271/first-slice): Old pattern: reverse-relayed child WorkflowRunState roots
        // could overwrite the parent actor current-state document.
        // New principle: actor-scoped current-state readmodels only materialize facts committed by
        // the same authoritative actor as the projection scope; relayed child facts stay observable artifacts.
        if (!string.Equals(context.RootActorId, publisherActorId, StringComparison.Ordinal))
            return null;

        var stateEvent = input.StateEvent;
        WorkflowCompensationMetrics.ObserveCommittedPayload(stateEvent.EventData);

        var state = input.State;
        var seedSnapshot = _forkSeedMapper.ToProjectionSnapshot(state);

        // Refactor (iter97/cluster-591): Old/New
        //   Old: every current-state projector hand-rolled committed-state unpack, timestamp resolution, and upsert.
        //   New: core mapped helper owns that projection shell; this projector keeps only WorkflowRunState -> read model mapping.
        var document = new WorkflowExecutionCurrentStateDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId,
            WorkflowId = state.WorkflowId ?? string.Empty,
            RevisionId = state.RevisionId ?? string.Empty,
            DefinitionVersion = Math.Max(0, state.DefinitionVersion),
            WorkflowName = state.WorkflowName ?? string.Empty,
            Status = ResolveCurrentStateStatus(state),
            ScopeId = state.ScopeId ?? string.Empty,
            RunOrigin = state.RunOrigin ?? string.Empty,
            ScheduleId = state.ScheduleId ?? string.Empty,
            ExpectedExecutionMode = state.ExpectedExecutionMode,
            Compiled = state.Compiled,
            CompilationError = WorkflowAuditTextSanitizer.SanitizeForStorage(state.CompilationError),
            Input = state.Input ?? string.Empty,
            FinalOutput = state.FinalOutput ?? string.Empty,
            FinalError = WorkflowAuditTextSanitizer.SanitizeForStorage(state.FinalError),
            TerminalValueLifecycleFailureKind = state.TerminalValueLifecycleFailureKind,
            SagaStatus = state.SagaStatus,
            DeadLetterFailedCompensationStepId = state.DeadLetterFailedCompensationStepId ?? string.Empty,
            DeadLetterRemainingUncompensated = state.DeadLetterRemainingUncompensated,
            DeadLetterError = WorkflowAuditTextSanitizer.SanitizeForStorage(state.DeadLetterError),
            ExecutionStateCount = state.ExecutionStates.Count,
            Success = ResolveSuccess(state.Status),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = input.ObservedAt,
            CompletedAtUtcValue = state.CompletedAtUtc?.Clone(),
            ActivityInitiator = MapInitiator(state.Initiator),
            InputSummary = WorkflowAuditTextSanitizer.SanitizeForDisplay(state.Input, 240),
            ActivityCurrentStep = ResolveCurrentStep(state),
            ActivityFirstFailure = ResolveFirstFailure(state),
            ActivityWaiting = ResolveWaiting(state),
            WorkflowYaml = seedSnapshot.WorkflowYaml,
            InlineWorkflowYamls = seedSnapshot.InlineWorkflowYamls.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedVariables = seedSnapshot.Variables.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedCompletedStepIds = seedSnapshot.CompletedStepIds.ToList(),
            ForkSeedLastFailedStepId = seedSnapshot.LastFailedStepId,
            RecoveryCapability = BuildRecoveryCapability(state, seedSnapshot),
            Lineage = MapLineage(state.Lineage),
            ForkSeedIdempotencies = seedSnapshot.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => MapStepIdempotency(x.Value),
                StringComparer.Ordinal),
            InputFileRefs = seedSnapshot.InputFileRefs.Select(MapInputFileRef).ToList(),
            ConnectorApprovals = MapConnectorApprovals(state),
        };
        if (seedSnapshot.NormalizedValues != null)
            document.NormalizedForkSeed = seedSnapshot.NormalizedValues.Clone();
        if (state.CapabilityAdmissionPlan is not null)
            document.CapabilityAdmissionPlan = state.CapabilityAdmissionPlan.Clone();

        // Fix (review round 1, F1):
        //   Projection previously materialized absent terminal duration as scalar zero.
        //   Forward the optional duration only when the authoritative state carries it.
        if (state.HasDurationMs)
            document.DurationMs = state.DurationMs;

        // O2 (06-19-workflow-run-observatory): started_at is derived from the committed WorkflowRunState's
        // own start fact (StartedAtUtc), so the projector stays a pure committed-state -> readmodel mapper
        // (no prior-readmodel read). Absent for pre-existing runs -> run list falls back to updated_at.
        if (state.StartedAtUtc != null)
            document.StartedAtUtcValue = state.StartedAtUtc;

        return document;
    }

    private static WorkflowRunActivityInitiatorReadModel MapInitiator(WorkflowRunInitiatorState? initiator)
    {
        if (initiator == null)
        {
            return new WorkflowRunActivityInitiatorReadModel
            {
                Availability = "unavailable",
                DisplayValue = "Unknown",
            };
        }

        return new WorkflowRunActivityInitiatorReadModel
        {
            Platform = WorkflowAuditTextSanitizer.Sanitize(initiator.Platform),
            Tenant = WorkflowAuditTextSanitizer.Sanitize(initiator.Tenant),
            ExternalUserId = WorkflowAuditTextSanitizer.Sanitize(initiator.ExternalUserId),
            Scope = WorkflowAuditTextSanitizer.Sanitize(initiator.Scope),
            // Never publish the NyxID binding handle. It can be exchanged for
            // a short-lived credential by trusted server-side infrastructure.
            BindingId = string.Empty,
            DisplayValue = string.IsNullOrWhiteSpace(initiator.DisplayValue)
                ? "Unknown"
                : WorkflowAuditTextSanitizer.Sanitize(initiator.DisplayValue),
            Availability = string.IsNullOrWhiteSpace(initiator.Availability)
                ? "unavailable"
                : WorkflowAuditTextSanitizer.Sanitize(initiator.Availability),
        };
    }

    private static WorkflowRunLineage MapLineage(WorkflowRunLineage? lineage) =>
        lineage?.Clone() ?? new WorkflowRunLineage
        {
            Availability = WorkflowRunLineageAvailability.Unavailable,
            UnavailableReason = "Run lineage is unavailable for this run.",
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
            SubWorkflow = new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
        };

    private static WorkflowRunActivityStepReadModel ResolveCurrentStep(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(WorkflowExecutionKernelState.Descriptor))
                continue;

            var kernelState = executionState.Unpack<WorkflowExecutionKernelState>();
            if (kernelState == null || string.IsNullOrWhiteSpace(kernelState.CurrentStepId))
                break;

            return new WorkflowRunActivityStepReadModel
            {
                StepId = WorkflowAuditTextSanitizer.Sanitize(kernelState.CurrentStepId),
                InputSummary = WorkflowAuditTextSanitizer.SanitizeForDisplay(
                    WorkflowNormalizedExecutionSeedCodec.ResolveCurrentInput(kernelState),
                    160),
                Availability = "available",
            };
        }

        return new WorkflowRunActivityStepReadModel
        {
            Availability = "unavailable",
        };
    }

    private static WorkflowRunActivityFailureReadModel ResolveFirstFailure(WorkflowRunState state)
    {
        var message = !string.IsNullOrWhiteSpace(state.FinalError)
            ? state.FinalError
            : state.DeadLetterError;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new WorkflowRunActivityFailureReadModel
            {
                Availability = "unavailable",
            };
        }

        return new WorkflowRunActivityFailureReadModel
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(ResolveFailureStepId(state)),
            Message = WorkflowAuditTextSanitizer.SanitizeForDisplay(
                WorkflowAuditTextSanitizer.SanitizeForStorage(message),
                240),
            Availability = "available",
        };
    }

    private static string ResolveFailureStepId(WorkflowRunState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CompensationOriginFailedStepId))
            return state.CompensationOriginFailedStepId;
        if (!string.IsNullOrWhiteSpace(state.DeadLetterFailedCompensationStepId))
            return state.DeadLetterFailedCompensationStepId;

        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (executionState?.Is(WorkflowExecutionKernelState.Descriptor) == true)
                return executionState.Unpack<WorkflowExecutionKernelState>().CurrentStepId?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static WorkflowRunActivityWaitingReadModel ResolveWaiting(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (executionState.Is(ToolCallModuleState.Descriptor))
            {
                var toolCallState = executionState.Unpack<ToolCallModuleState>();
                var pending = toolCallState?.PendingApprovals.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "tool_approval", $"Approve {pending.ToolName}");
            }

            if (executionState.Is(HumanApprovalModuleState.Descriptor))
            {
                var approvalState = executionState.Unpack<HumanApprovalModuleState>();
                var pending = approvalState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "human_approval", pending.Input);
            }

            if (executionState.Is(HumanInputModuleState.Descriptor))
            {
                var humanInputState = executionState.Unpack<HumanInputModuleState>();
                var pending = humanInputState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "human_input", pending.Input);
            }

            if (executionState.Is(WaitSignalModuleState.Descriptor))
            {
                var signalState = executionState.Unpack<WaitSignalModuleState>();
                var pending = signalState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "signal", pending.SignalName);
            }

            if (executionState.Is(SecureInputModuleState.Descriptor))
            {
                var secureInputState = executionState.Unpack<SecureInputModuleState>();
                var pending = secureInputState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "secure_input", pending.Input);
            }

            if (executionState.Is(DelayModuleState.Descriptor))
            {
                var delayState = executionState.Unpack<DelayModuleState>();
                if (delayState?.Pending.Count > 0)
                {
                    var pending = delayState.Pending
                        .OrderBy(static item => item.Key, StringComparer.Ordinal)
                        .First();
                    return Waiting(ResolveDelayWaitingStepId(pending.Key, pending.Value), "delay", pending.Value.Input);
                }
            }
        }

        return new WorkflowRunActivityWaitingReadModel
        {
            Availability = "unavailable",
        };
    }

    private static string ResolveDelayWaitingStepId(string pendingKey, PendingDelayState pending)
    {
        var stepId = pending.StepId?.Trim();
        if (!string.IsNullOrWhiteSpace(stepId))
            return stepId;

        var separatorIndex = pendingKey.LastIndexOf(':');
        return separatorIndex >= 0 ? pendingKey[(separatorIndex + 1)..].Trim() : string.Empty;
    }

    private static WorkflowRunActivityWaitingReadModel Waiting(string? stepId, string waitingKind, string? prompt) =>
        new()
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(stepId),
            WaitingKind = WorkflowAuditTextSanitizer.Sanitize(waitingKind),
            Prompt = WorkflowAuditTextSanitizer.SanitizeForDisplay(prompt, 160),
            Availability = "available",
        };

    private static IList<WorkflowExternalActionApprovalSnapshot> MapConnectorApprovals(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(ConnectorCallModuleState.Descriptor))
                continue;

            var connectorState = executionState.Unpack<ConnectorCallModuleState>();
            return connectorState.ApprovalsByActionId.Values
                .Where(static coordination => coordination.Snapshot?.Plan != null)
                .OrderBy(static coordination => coordination.Snapshot.Plan.ActionId, StringComparer.Ordinal)
                .Select(static coordination => coordination.Snapshot.Clone())
                .ToList();
        }

        return [];
    }

    private static string ResolveCurrentStateStatus(WorkflowRunState state)
    {
        var status = state.Status ?? string.Empty;
        if (status is "completed" or "timed_out" or "failed" or "stopped")
            return status;

        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(ToolCallModuleState.Descriptor))
                continue;

            if (executionState.Unpack<ToolCallModuleState>().PendingApprovals.Count > 0)
                return "awaiting_tool_approval";
        }

        return status;
    }

    private static WorkflowStepIdempotencyReadModel MapStepIdempotency(
        WorkflowStepIdempotencyState source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowStepIdempotencyReadModel
        {
            LogicalRunId = source.LogicalRunId ?? string.Empty,
            StepId = source.StepId ?? string.Empty,
            LogicalAttempt = source.LogicalAttempt,
            IdempotencyKey = source.IdempotencyKey ?? string.Empty,
        };
    }

    private static WorkflowExecutionInputFileRefReadModel MapInputFileRef(WorkflowFileRef source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionInputFileRefReadModel
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKindValue = (int)source.SourceKind,
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };
    }

    private static bool? ResolveSuccess(string? status)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "completed" => true,
            "timed_out" => false,
            "failed" => false,
            _ => null,
        };
    }

    // Implement (issue #3251):
    //   Behavior: recovery availability is a typed read-model fact, not UI inference from failure text or actor ids.
    //   Why this shape: the projector has committed run state plus fork seed facts and can publish conservative capabilities.
    private WorkflowRunRecoveryCapabilityReadModel BuildRecoveryCapability(
        WorkflowRunState state,
        WorkflowRunForkSeedProjectionSnapshot seedSnapshot)
    {
        var revisionId = state.RevisionId ?? string.Empty;
        var definitionVersion = Math.Max(0, state.DefinitionVersion);
        return new WorkflowRunRecoveryCapabilityReadModel
        {
            WorkflowDefinitionRevisionId = revisionId,
            WorkflowDefinitionVersion = definitionVersion,
            RetryFailedStep = BuildRetryFailedStepCapability(state, seedSnapshot),
            RunAgain = BuildRunAgainCapability(state, seedSnapshot),
        };
    }

    private WorkflowRecoveryActionCapabilityReadModel BuildRetryFailedStepCapability(
        WorkflowRunState state,
        WorkflowRunForkSeedProjectionSnapshot seedSnapshot)
    {
        var status = Normalize(state.Status);
        var failureClass = ClassifyFailure(state);
        if (failureClass.ReasonCode != WorkflowRecoveryUnavailableReasonCodeReadModel.None)
            return Ineligible(failureClass.ReasonCode, failureClass.Reason, failureClass.RecommendedAction);

        if (!string.Equals(status, FailedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return Ineligible(
                WorkflowRecoveryUnavailableReasonCodeReadModel.SourceRunNotTerminal,
                "Retry from failed step is available only after a failed terminal run.",
                WorkflowRecoveryRecommendedActionReadModel.TechnicalDetails);
        }

        if (string.IsNullOrWhiteSpace(seedSnapshot.WorkflowYaml))
        {
            return Unavailable(
                WorkflowRecoveryUnavailableReasonCodeReadModel.MissingSourceFact,
                "The source workflow definition is not available for retry.",
                WorkflowRecoveryRecommendedActionReadModel.EditWorkflow);
        }

        if (string.IsNullOrWhiteSpace(seedSnapshot.LastFailedStepId))
        {
            return Unavailable(
                WorkflowRecoveryUnavailableReasonCodeReadModel.LegacyUnavailable,
                "The failed step was not materialized for this run.",
                WorkflowRecoveryRecommendedActionReadModel.TechnicalDetails);
        }

        return Eligible(
            WorkflowRecoveryRecommendedActionReadModel.Retry,
            seedSnapshot.LastFailedStepId,
            reusesPriorStepOutputs: true,
            mayIncurModelOrToolCost: true);
    }

    private WorkflowRecoveryActionCapabilityReadModel BuildRunAgainCapability(
        WorkflowRunState state,
        WorkflowRunForkSeedProjectionSnapshot seedSnapshot)
    {
        var status = Normalize(state.Status);
        var failureClass = ClassifyFailure(state);
        if (failureClass.ReasonCode != WorkflowRecoveryUnavailableReasonCodeReadModel.None)
            return Ineligible(failureClass.ReasonCode, failureClass.Reason, failureClass.RecommendedAction);

        if (status is not (FailedStatus or CompletedStatus or StoppedStatus))
        {
            return Ineligible(
                WorkflowRecoveryUnavailableReasonCodeReadModel.SourceRunNotTerminal,
                "Run again is available only after the source run reaches a terminal state.",
                WorkflowRecoveryRecommendedActionReadModel.TechnicalDetails);
        }

        if (string.IsNullOrWhiteSpace(seedSnapshot.WorkflowYaml))
        {
            return Unavailable(
                WorkflowRecoveryUnavailableReasonCodeReadModel.WorkflowDefinitionUnavailable,
                "The source workflow definition is not available for run again.",
                WorkflowRecoveryRecommendedActionReadModel.EditWorkflow);
        }

        if (!TryResolveEntryStepId(seedSnapshot.WorkflowYaml, out var entryStepId))
        {
            return Unavailable(
                WorkflowRecoveryUnavailableReasonCodeReadModel.WorkflowDefinitionUnavailable,
                "The source workflow definition entry step is not available for run again.",
                WorkflowRecoveryRecommendedActionReadModel.EditWorkflow);
        }

        return Eligible(
            WorkflowRecoveryRecommendedActionReadModel.RunAgain,
            entryStepId,
            reusesPriorStepOutputs: false,
            mayIncurModelOrToolCost: true);
    }

    private bool TryResolveEntryStepId(string workflowYaml, out string entryStepId)
    {
        try
        {
            entryStepId = _workflowParser.Parse(workflowYaml).EntryStepId?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(entryStepId);
        }
        catch (InvalidOperationException)
        {
            entryStepId = string.Empty;
            return false;
        }
        catch (YamlException)
        {
            entryStepId = string.Empty;
            return false;
        }
    }

    private static WorkflowRecoveryActionCapabilityReadModel Eligible(
        WorkflowRecoveryRecommendedActionReadModel recommendedAction,
        string startingStepId,
        bool reusesPriorStepOutputs,
        bool mayIncurModelOrToolCost)
    {
        var capability = new WorkflowRecoveryActionCapabilityReadModel
        {
            Eligibility = WorkflowRecoveryEligibilityReadModel.Eligible,
            UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCodeReadModel.None,
            StartingStepId = WorkflowAuditTextSanitizer.Sanitize(startingStepId),
            ReusesPriorStepOutputs = reusesPriorStepOutputs,
            MayIncurModelOrToolCost = mayIncurModelOrToolCost,
        };
        capability.RecommendedActions.Add(recommendedAction);
        return capability;
    }

    private static WorkflowRecoveryActionCapabilityReadModel Ineligible(
        WorkflowRecoveryUnavailableReasonCodeReadModel reasonCode,
        string unavailableReason,
        WorkflowRecoveryRecommendedActionReadModel recommendedAction) =>
        NotEligible(
            WorkflowRecoveryEligibilityReadModel.Ineligible,
            reasonCode,
            unavailableReason,
            recommendedAction);

    private static WorkflowRecoveryActionCapabilityReadModel Unavailable(
        WorkflowRecoveryUnavailableReasonCodeReadModel reasonCode,
        string unavailableReason,
        WorkflowRecoveryRecommendedActionReadModel recommendedAction) =>
        NotEligible(
            WorkflowRecoveryEligibilityReadModel.Unavailable,
            reasonCode,
            unavailableReason,
            recommendedAction);

    private static WorkflowRecoveryActionCapabilityReadModel NotEligible(
        WorkflowRecoveryEligibilityReadModel eligibility,
        WorkflowRecoveryUnavailableReasonCodeReadModel reasonCode,
        string unavailableReason,
        WorkflowRecoveryRecommendedActionReadModel recommendedAction)
    {
        var capability = new WorkflowRecoveryActionCapabilityReadModel
        {
            Eligibility = eligibility,
            UnavailableReasonCode = reasonCode,
            UnavailableReason = WorkflowAuditTextSanitizer.SanitizeForDisplay(unavailableReason, 240),
        };
        capability.RecommendedActions.Add(recommendedAction);
        return capability;
    }

    private static WorkflowFailureClassification ClassifyFailure(WorkflowRunState state)
    {
        // Fix (review round 1, F1):
        //   Recovery classification previously inspected FinalError substrings.
        //   Map only the typed committed failure kind owned by the run state/event path.
        return state.TerminalRecoveryFailureKind switch
        {
            WorkflowRecoveryFailureKind.AuthorizationFailure => new WorkflowFailureClassification(
                WorkflowRecoveryUnavailableReasonCodeReadModel.AuthorizationFailure,
                "Access must be fixed before this run can be recovered.",
                WorkflowRecoveryRecommendedActionReadModel.FixAccess),
            WorkflowRecoveryFailureKind.ConfigurationFailure => new WorkflowFailureClassification(
                WorkflowRecoveryUnavailableReasonCodeReadModel.ConfigurationFailure,
                "Configuration must be changed before this run can be recovered.",
                WorkflowRecoveryRecommendedActionReadModel.ChangeConfiguration),
            _ => WorkflowFailureClassification.None,
        };
    }

    private static string Normalize(string? value) =>
        value?.Trim() ?? string.Empty;

    private readonly record struct WorkflowFailureClassification(
        WorkflowRecoveryUnavailableReasonCodeReadModel ReasonCode,
        string Reason,
        WorkflowRecoveryRecommendedActionReadModel RecommendedAction)
    {
        public static WorkflowFailureClassification None =>
            new(
                WorkflowRecoveryUnavailableReasonCodeReadModel.None,
                string.Empty,
                WorkflowRecoveryRecommendedActionReadModel.Unspecified);
    }
}
