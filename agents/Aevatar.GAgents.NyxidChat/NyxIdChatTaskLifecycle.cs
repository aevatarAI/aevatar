using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatTaskLifecycleDecision(
    NyxIdChatTransitionOutcome Outcome,
    string ReasonCode,
    string SafeMessage,
    NyxIdChatConversationGAgentState State,
    NyxIdChatOperationDispatchCommand? NextCommand,
    NyxIdChatInputRequestCommand? InputRequest = null);

/// <summary>
/// Pure actor-owned derivation for one reconciled NyxIdChat operation. The
/// returned successor command is an actor-turn-local dispatch instruction; it
/// must never be copied into durable state or a committed event.
/// </summary>
public static class NyxIdChatTaskLifecycle
{
    private const int DefaultToolEstimateSeconds = 60;
    private const int DefaultVerificationEstimateSeconds = 30;
    public const string ToolSafetyRequired = "NYXID_CHAT_TOOL_SAFETY_REQUIRED";
    public const string ToolCallInvalid = "NYXID_CHAT_TOOL_CALL_INVALID";
    public const string ToolAdmissionInvalid = "NYXID_CHAT_TOOL_ADMISSION_INVALID";
    public const string ToolVerificationEvidenceMismatch =
        "NYXID_CHAT_TOOL_VERIFICATION_EVIDENCE_MISMATCH";
    public const string MultipleToolCallsUnsupported =
        "NYXID_CHAT_MULTIPLE_TOOL_CALLS_UNSUPPORTED";
    public const string InputRequestInvalid = "NYXID_CHAT_INPUT_REQUEST_INVALID";
    public const string ApprovalExpired = "NYXID_CHAT_APPROVAL_EXPIRED";
    public const string ConditionProposalInvalid = "NYXID_CHAT_CONDITION_PROPOSAL_INVALID";
    public const string ConditionSourceStale = "NYXID_CHAT_CONDITION_SOURCE_STALE";
    public const string ConditionGuardMismatch = "NYXID_CHAT_CONDITION_GUARD_MISMATCH";
    public const string ConditionGuardedToolRequired =
        "NYXID_CHAT_CONDITION_GUARDED_TOOL_REQUIRED";
    public const string ServiceConnectCatalogRequired =
        "NYXID_CHAT_SERVICE_CONNECT_CATALOG_REQUIRED";
    public const string ServiceConnectToolInvalid =
        "NYXID_CHAT_SERVICE_CONNECT_TOOL_INVALID";
    public const string ServiceConnectPostconditionRequired =
        "NYXID_CHAT_SERVICE_CONNECT_POSTCONDITION_REQUIRED";
    public const string ServiceConnectPostconditionEvidenceMismatch =
        "NYXID_CHAT_SERVICE_CONNECT_POSTCONDITION_EVIDENCE_MISMATCH";
    public const string AuthorizationContinuationToolRequired =
        "NYXID_AUTHORIZATION_CONTINUATION_TOOL_REQUIRED";
    internal const string AuthorizationContinuationCapabilityUnavailable =
        "NYXID_AUTHORIZATION_CONTINUATION_CAPABILITY_UNAVAILABLE";

    private const string NyxIdCatalogToolName = "nyxid_catalog";
    private const string NyxIdRequireServiceToolName = "nyxid_require_service";
    private const string ServiceConnectedCheck = "service.connected";

    private const string ToolSafetyRequiredMessage =
        "The authorized tool provider did not supply the required safety classification.";
    private const string ToolCallInvalidMessage =
        "The model returned an invalid typed tool call.";
    private const string ToolAdmissionInvalidMessage =
        "The connected-service operation admission was incomplete or inconsistent.";
    private const string MultipleToolCallsUnsupportedMessage =
        "This chat version can execute only one authorized tool call at a time.";
    private const string InputRequestInvalidMessage =
        "The assistant returned an invalid structured input request.";
    private const string ConditionProposalInvalidMessage =
        "The assistant returned an invalid typed condition proposal.";
    private const string ConditionSourceStaleMessage =
        "The condition did not reference the active committed numeric input.";
    private const string ConditionGuardMismatchMessage =
        "The proposed tool did not match the committed condition guard.";
    private const string ConditionGuardedToolRequiredMessage =
        "The true condition requires the exact guarded tool call.";
    private const string AuthorizationContinuationToolRequiredMessage =
        "The verified connected-service request did not invoke an available operation.";
    internal const string AuthorizationContinuationCapabilityUnavailableMessage =
        "The verified NyxID service has no exact operation available for the original request.";
    internal const string ApprovalExpiredMessage =
        "The tool approval expired before a decision was committed.";

    // Deadline the actor stamps on every local pending tool approval it parks.
    // The actor is the authoritative source for actor-owned approvals; admitted
    // Tier B connected-service approvals are post-return observations. Tier A
    // exact-service approvals park the actor-owned continuation before effect.
    public static readonly TimeSpan ToolApprovalExpiryWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reconciles one operation result and records its durable ledger facts.
    /// </summary>
    /// <remarks>
    /// Recording wraps every reconciliation branch, including a model reply whose
    /// only output is a tool call, so the transcript keeps one record per Model
    /// and Tool operation exactly as the live trajectory renders them.
    /// </remarks>
    public static NyxIdChatTaskLifecycleDecision ApplyOperationResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        Timestamp now)
    {
        var decision = ReconcileOperationResult(state, signal, now);
        if (decision.Outcome == NyxIdChatTransitionOutcome.Accepted)
            NyxIdChatOperationLedger.RecordResult(decision.State, signal);
        return decision;
    }

    private static NyxIdChatTaskLifecycleDecision ReconcileOperationResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        var currentStep = FindCurrentStep(state, signal.Key);
        if (currentStep is null)
            return FromTransition(
                NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal),
                nextCommand: null);

        var operationKey = signal.Key!;

        if (signal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification &&
            currentStep.Kind == NyxIdChatStepKind.Tool)
        {
            return ApplyRecoveredToolVerification(
                state,
                currentStep,
                signal.ToolVerification,
                now);
        }

        if (signal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification &&
            !MatchesFrozenVerification(currentStep, signal.ToolVerification))
        {
            return new NyxIdChatTaskLifecycleDecision(
                NyxIdChatTransitionOutcome.Rejected,
                ToolVerificationEvidenceMismatch,
                "The verification evidence did not match the frozen postcondition.",
                state.Clone(),
                NextCommand: null);
        }

        if (signal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition &&
            IsServiceConnectedPostcondition(currentStep) &&
            !MatchesServiceConnectedPostcondition(currentStep, signal.ActionPostcondition))
        {
            return new NyxIdChatTaskLifecycleDecision(
                NyxIdChatTransitionOutcome.Rejected,
                ServiceConnectPostconditionEvidenceMismatch,
                "The service connection evidence did not match the frozen UserService identity.",
                state.Clone(),
                NextCommand: null);
        }

        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            signal.Llm.ToolCalls.Count > 0)
        {
            return ApplyLlmToolPlan(
                state,
                signal,
                operationKey,
                currentStep,
                now);
        }

        var verifiedAuthorizationCompletionCommunication = false;
        var authorizationResumeRequirement =
            currentStep.Source?.Llm?.ResumeRequirement ??
            NyxIdChatAuthorizationResumeRequirement.Unspecified;
        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            authorizationResumeRequirement is
                NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest or
                NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion)
        {
            if (!NyxIdChatActionContinuationCorrelation.TryMatch(
                    state,
                    state.ActiveTask,
                    state.ActiveTurn,
                    operationKey,
                    out var correlation))
            {
                return RejectAuthorizationContinuation(state);
            }

            if (authorizationResumeRequirement ==
                NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest)
            {
                return ApplyIncompleteAuthorizationContinuation(
                    state,
                    signal,
                    operationKey,
                    currentStep,
                    correlation,
                    now);
            }

            verifiedAuthorizationCompletionCommunication = true;
        }

        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            IsServiceConnectIntent(state) &&
            !verifiedAuthorizationCompletionCommunication &&
            !HasVerifiedServiceConnectedPostcondition(state))
        {
            return FailClosed(
                state,
                operationKey,
                ServiceConnectPostconditionRequired,
                "The service connection must be verified through its typed postcondition.",
                now);
        }

        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            TryResolveConditionGuard(state, currentStep, out var conditionStep, out var guardedStep))
        {
            if (conditionStep.Source.Condition.Condition.Outcome ==
                NyxIdChatConditionOutcome.True)
            {
                return FailConditionGuard(
                    state,
                    operationKey,
                    guardedStep,
                    ConditionGuardedToolRequired,
                    ConditionGuardedToolRequiredMessage,
                    now);
            }
        }

        var normalizedSignal = NormalizeToolResult(signal, currentStep);
        var reconciliationState = DisableRetryForUnavailableAuthorizationContinuation(
            state,
            currentStep,
            normalizedSignal);
        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(
            reconciliationState,
            normalizedSignal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        ApplyApprovalObservation(next, normalizedSignal, currentStep, now);
        ApplyConnectedServiceApprovalReentry(next, normalizedSignal, currentStep, now);

        if (normalizedSignal.ResultCase ==
            NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification)
        {
            ApplyVerificationEvidence(next, normalizedSignal.ToolVerification, now);
        }

        NyxIdChatOperationDispatchCommand? successor = null;
        if (normalizedSignal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.Tool &&
            normalizedSignal.Tool.Receipt?.Status == AgentToolReceiptStatus.Success)
        {
            BindProviderResourceIdentity(
                next,
                operationKey.StepId,
                normalizedSignal.Tool.Receipt.ProviderResourceId);
            successor = ActivatePlannedVerificationStep(
                next,
                normalizedSignal.Key,
                now,
                normalizedSignal.Tool.Receipt.MutationStage ==
                AgentToolReceiptMutationStage.ReadModelObserved);
        }
        else if (currentStep.Kind == NyxIdChatStepKind.Tool &&
                 FindCurrentStep(next, operationKey)?.Status is
                     NyxIdChatStepStatus.Failed or
                     NyxIdChatStepStatus.Cancelled or
                     NyxIdChatStepStatus.Uncertain)
        {
            if (FindCurrentStep(next, operationKey)?.ExternalEffect ==
                    NyxIdChatEffectEvidence.MayHaveChanged)
            {
                successor = ReplanFailureRecoveryVerificationStep(next, operationKey, now);
            }
            else
            {
                CancelPlannedVerificationStep(next, operationKey.StepId, now);
            }
        }

        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            successor);
    }

    private static NyxIdChatConversationGAgentState DisableRetryForUnavailableAuthorizationContinuation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatOperationResultSignal signal)
    {
        if (currentStep.Kind != NyxIdChatStepKind.Tool ||
            signal.ResultCase != NyxIdChatOperationResultSignal.ResultOneofCase.Failure ||
            !string.Equals(
                signal.Failure.FailureCode,
                AuthorizationContinuationCapabilityUnavailable,
                StringComparison.Ordinal) ||
            !NyxIdChatActionContinuationCorrelation.TryMatch(
                state,
                state.ActiveTask,
                state.ActiveTurn,
                signal.Key,
                out _))
        {
            return state;
        }

        var next = state.Clone();
        var step = FindCurrentStep(next, signal.Key);
        if (step is null)
            return state;

        step.RetryInputRebuildable = false;
        step.RetryToolInput = null;
        step.RematerializeDurableAuthorization = false;
        step.RetryAuthorizationSourceKey = null;
        return next;
    }

    private static NyxIdChatTaskLifecycleDecision ApplyIncompleteAuthorizationContinuation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatActionContinuationCorrelationMatch correlation,
        Timestamp now)
    {
        var actionRequestId = currentStep.Source?.Llm?.ActionRequestId;
        var continuationCount = state.ActiveTask.Steps.Count(step =>
            step.Kind == NyxIdChatStepKind.Llm &&
            string.Equals(
                step.Source?.Llm?.ActionRequestId,
                actionRequestId,
                StringComparison.Ordinal) &&
            step.Source?.Llm?.ResumeRequirement ==
            NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest);
        if (continuationCount >= 2)
        {
            return FailClosed(
                state,
                operationKey,
                AuthorizationContinuationToolRequired,
                AuthorizationContinuationToolRequiredMessage,
                now);
        }

        var verifiedAuthorization =
            NyxIdChatActionContinuationCorrelation.BuildVerifiedAuthorizationContinuation(
                correlation,
                now);
        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var correctionOrdinal = checked(continuationCount + 1);
        var order = next.ActiveTask.Steps.Max(static step => step.Order) + 1;
        var stepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            actionRequestId!,
            correctionOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "authorization-continuation-correction");
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = next.ConversationActorId,
            TurnId = operationKey.TurnId,
            TaskId = operationKey.TaskId,
            StepId = stepId,
            OperationId = BuildStableIdentity(
                "operation",
                next.ConversationActorId,
                operationKey.TurnId,
                operationKey.TaskId,
                stepId,
                "1"),
            OperationGeneration = 1,
        };
        var correctiveStep = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description =
                "Retry the original request with the exact verified connected-service operation.",
            Source = new NyxIdChatStepSource
            {
                Llm = currentStep.Source!.Llm.Clone(),
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { correlation.PostconditionStep.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        correctiveStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(correctiveStep);
        next.ActiveTask.Steps.Add(correctiveStep);
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.FailureRecovery,
            now,
            [correctiveStep]);
        ActivateStep(next, correctiveStep, now);
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            new NyxIdChatOperationDispatchCommand
            {
                Key = key,
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = next.AgentProfile?.Clone(),
                    AgentProfileTurnAuthority =
                        next.ActiveTurn.AgentProfileTurnAuthority?.Clone(),
                    Intent = next.ActiveTurn.Intent,
                    VerifiedAuthorizationContinuation = verifiedAuthorization,
                },
            });
    }

    private static NyxIdChatTaskLifecycleDecision RejectAuthorizationContinuation(
        NyxIdChatConversationGAgentState state) =>
        new(
            NyxIdChatTransitionOutcome.Rejected,
            NyxIdChatBrowserActions.ActionContinuationInvalid,
            "The verified NyxID authorization continuation did not match committed actor state.",
            state.Clone(),
            NextCommand: null);

    private static NyxIdChatTaskLifecycleDecision ApplyLlmToolPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        Timestamp now)
    {
        if (signal.Llm.ToolCalls.Count != 1)
        {
            return FailClosed(
                state,
                operationKey,
                MultipleToolCallsUnsupported,
                MultipleToolCallsUnsupportedMessage,
                now);
        }

        var toolCall = signal.Llm.ToolCalls[0];
        if (string.IsNullOrWhiteSpace(toolCall.CallId) ||
            string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return FailClosed(
                state,
                operationKey,
                ToolCallInvalid,
                ToolCallInvalidMessage,
                now);
        }

        if (toolCall.Safety is null)
        {
            return FailClosed(
                state,
                operationKey,
                ToolSafetyRequired,
                ToolSafetyRequiredMessage,
                now);
        }

        if (NyxIdChatAskUserContract.IsAskUser(toolCall))
            return ApplyLlmInputPlan(state, signal, operationKey, currentStep, toolCall, now);

        if (NyxIdChatConditionEvaluateContract.IsConditionEvaluate(toolCall))
            return ApplyLlmConditionPlan(state, signal, operationKey, currentStep, toolCall, now);

        if (TryResolveConditionGuard(state, currentStep, out var conditionStep, out var guardedStep))
        {
            return ApplyGuardedToolPlan(
                state,
                signal,
                operationKey,
                currentStep,
                conditionStep,
                guardedStep,
                toolCall,
                now);
        }

        NyxIdCatalogServiceConnectParams? serviceConnectPostcondition = null;
        if (IsServiceConnectIntent(state))
        {
            if (!IsServiceConnectTool(toolCall.ToolName))
            {
                return FailClosed(
                    state,
                    operationKey,
                    ServiceConnectToolInvalid,
                    "The admitted service-connect turn selected an unauthorized tool.",
                    now);
            }

            if (string.Equals(
                    toolCall.ToolName,
                    NyxIdRequireServiceToolName,
                    StringComparison.Ordinal))
            {
                if (!HasCompletedCatalogStep(state))
                {
                    return FailClosed(
                        state,
                        operationKey,
                        ServiceConnectCatalogRequired,
                        "The exact NyxID catalog entry must be observed before checking service readiness.",
                        now);
                }

                if (!TryParseServiceConnectPostcondition(
                        toolCall.ArgumentsJson,
                        out serviceConnectPostcondition))
                {
                    return FailClosed(
                        state,
                        operationKey,
                        ToolCallInvalid,
                        ToolCallInvalidMessage,
                        now);
                }
            }
        }

        if (NyxIdChatOperationAdmissionPolicy.IsConnectedServiceCall(toolCall) &&
            !NyxIdChatOperationAdmissionPolicy.IsValid(
                toolCall.OperationAdmission,
                toolCall.Safety,
                toolCall.NyxIdProvenance))
        {
            return FailClosed(
                state,
                operationKey,
                ToolAdmissionInvalid,
                ToolAdmissionInvalidMessage,
                now);
        }

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var toolStep = BuildToolStep(
            next,
            currentStep,
            toolCall,
            now);
        if (serviceConnectPostcondition is not null)
        {
            toolStep.Source.Tool.ServiceConnectPostcondition =
                serviceConnectPostcondition.Clone();
        }
        var verificationStep = BuildVerificationStep(next, toolStep, now);
        next.ActiveTask.Steps.Add(toolStep);
        next.ActiveTask.Steps.Add(verificationStep);
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            [toolStep, verificationStep]);

        ActivateStep(next, toolStep, now);

        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = toolCall.CallId,
                ToolName = toolCall.ToolName,
                ArgumentsJson = toolCall.ArgumentsJson,
                MayChangeExternalState = toolCall.Safety.MayChangeExternalState,
                Idempotent = !toolCall.Safety.MayChangeExternalState,
                IdempotencyKey = toolStep.Operation.IdempotencyKey,
                OperationAdmission = toolCall.OperationAdmission?.Clone(),
                Intent = next.ActiveTurn.Intent,
                Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                    toolCall.Presentation,
                    toolCall.ToolName),
            },
        };
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            command);
    }

    private static NyxIdChatTaskLifecycleDecision ApplyLlmConditionPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatToolCall toolCall,
        Timestamp now)
    {
        if (!NyxIdChatConditionEvaluateContract.TryParse(
                toolCall.ArgumentsJson,
                out var proposal))
        {
            return FailClosed(
                state,
                operationKey,
                ConditionProposalInvalid,
                ConditionProposalInvalidMessage,
                now);
        }

        var inputStep = state.ActiveTask.Steps.SingleOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Input &&
            step.Status == NyxIdChatStepStatus.Done &&
            string.Equals(step.Source?.Input?.RequestId, proposal.SourceInputRequestId,
                StringComparison.Ordinal));
        var resolution = state.RecentInputResolutions.SingleOrDefault(candidate =>
            string.Equals(candidate.RequestId, proposal.SourceInputRequestId,
                StringComparison.Ordinal));
        if (inputStep is null ||
            resolution?.NumericThreshold is null ||
            !currentStep.DependsOn.Contains(inputStep.StepId, StringComparer.Ordinal))
        {
            return FailClosed(
                state,
                operationKey,
                ConditionSourceStale,
                ConditionSourceStaleMessage,
                now);
        }

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        next.ActiveTask.SchemaVersion = Math.Max(next.ActiveTask.SchemaVersion, 5);
        var conditionOrder = next.ActiveTask.Steps.Max(static step => step.Order) + 1;
        var conditionStepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            currentStep.StepId,
            toolCall.CallId,
            conditionOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "condition");
        var conditionId = BuildStableIdentity(
            "condition",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            conditionStepId,
            proposal.SourceInputRequestId,
            toolCall.CallId);
        var outcome = proposal.ObservedValue >= resolution.NumericThreshold.EffectiveValue
            ? NyxIdChatConditionOutcome.True
            : NyxIdChatConditionOutcome.False;
        var condition = new NyxIdChatNumericConditionState
        {
            ConditionId = conditionId,
            SourceInputRequestId = proposal.SourceInputRequestId,
            SuggestedThreshold = resolution.NumericThreshold.SuggestedValue,
            EffectiveThreshold = resolution.NumericThreshold.EffectiveValue,
            ThresholdOrigin = resolution.NumericThreshold.Origin,
            ObservedValue = proposal.ObservedValue,
            Comparison = NyxIdChatIntegerComparison.Gte,
            Outcome = outcome,
            EvaluatedAt = now.Clone(),
            GuardedToolName = proposal.GuardedToolName,
        };
        var conditionStep = new NyxIdChatTaskStepState
        {
            StepId = conditionStepId,
            Order = conditionOrder,
            Kind = NyxIdChatStepKind.Condition,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Description = "Evaluate the committed numeric threshold condition.",
            Source = new NyxIdChatStepSource
            {
                Condition = new NyxIdChatConditionStepSource
                {
                    Condition = condition.Clone(),
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { inputStep.StepId, currentStep.StepId },
            UpdatedAt = now.Clone(),
        };
        conditionStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(conditionStep);

        var guardedOrder = conditionOrder + 1;
        var guardedStepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            conditionStepId,
            proposal.GuardedToolName,
            guardedOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "guarded-tool");
        var guardedStep = new NyxIdChatTaskStepState
        {
            StepId = guardedStepId,
            Order = guardedOrder,
            Kind = NyxIdChatStepKind.Tool,
            Status = outcome == NyxIdChatConditionOutcome.True
                ? NyxIdChatStepStatus.Planned
                : NyxIdChatStepStatus.Skipped,
            Required = true,
            Description = $"Run guarded tool {proposal.GuardedToolName}.",
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource { ToolName = proposal.GuardedToolName },
            },
            MayChangeExternalState = true,
            ExternalEffect = outcome == NyxIdChatConditionOutcome.True
                ? NyxIdChatEffectEvidence.NotStarted
                : NyxIdChatEffectEvidence.NotApplied,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { conditionStepId },
            Guard = new NyxIdChatStepGuard
            {
                ConditionStepId = conditionStepId,
                RequiredOutcome = NyxIdChatConditionOutcome.True,
            },
            UpdatedAt = now.Clone(),
        };
        guardedStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(guardedStep);

        NyxIdChatTaskStepState? skippedVerificationStep = null;
        var continuationOrder = guardedOrder + 1;
        if (outcome == NyxIdChatConditionOutcome.False)
        {
            skippedVerificationStep = BuildSkippedGuardedVerificationStep(
                next,
                guardedStep,
                continuationOrder,
                now);
            continuationOrder++;
        }
        var continuationStepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            conditionStepId,
            toolCall.CallId,
            continuationOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "condition-continuation");
        var continuationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = next.ConversationActorId,
            TurnId = operationKey.TurnId,
            TaskId = operationKey.TaskId,
            StepId = continuationStepId,
            OperationId = BuildStableIdentity(
                "operation",
                next.ConversationActorId,
                operationKey.TurnId,
                operationKey.TaskId,
                continuationStepId,
                "1"),
            OperationGeneration = 1,
        };
        var continuationStep = new NyxIdChatTaskStepState
        {
            StepId = continuationStepId,
            Order = continuationOrder,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Continue from the committed condition outcome.",
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { conditionStepId },
            Operation = new NyxIdChatOperationState
            {
                Key = continuationKey.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        continuationStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(continuationStep);

        next.ActiveTask.Steps.Add(conditionStep);
        next.ActiveTask.Steps.Add(guardedStep);
        if (skippedVerificationStep is not null)
            next.ActiveTask.Steps.Add(skippedVerificationStep);
        next.ActiveTask.Steps.Add(continuationStep);
        IReadOnlyList<NyxIdChatTaskStepState> addedSteps = skippedVerificationStep is null
            ? [conditionStep, guardedStep, continuationStep]
            : [conditionStep, guardedStep, skippedVerificationStep, continuationStep];
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            addedSteps);
        ActivateStep(next, continuationStep, now);
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            new NyxIdChatOperationDispatchCommand
            {
                Key = continuationKey,
                ConditionContinuation = new NyxIdChatConditionContinuationInput
                {
                    ToolCallId = toolCall.CallId,
                    Condition = condition.Clone(),
                },
            });
    }

    private static NyxIdChatTaskLifecycleDecision ApplyGuardedToolPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatTaskStepState conditionStep,
        NyxIdChatTaskStepState guardedStep,
        NyxIdChatToolCall toolCall,
        Timestamp now)
    {
        var condition = conditionStep.Source.Condition.Condition;
        if (condition.Outcome != NyxIdChatConditionOutcome.True ||
            guardedStep.Status != NyxIdChatStepStatus.Planned ||
            !string.Equals(toolCall.ToolName?.Trim(), condition.GuardedToolName,
                StringComparison.Ordinal))
        {
            return FailConditionGuard(
                state,
                operationKey,
                guardedStep,
                ConditionGuardMismatch,
                ConditionGuardMismatchMessage,
                now);
        }
        if (toolCall.Safety is null)
        {
            return FailConditionGuard(
                state,
                operationKey,
                guardedStep,
                ToolSafetyRequired,
                ToolSafetyRequiredMessage,
                now);
        }
        if (NyxIdChatOperationAdmissionPolicy.IsConnectedServiceCall(toolCall) &&
            !NyxIdChatOperationAdmissionPolicy.IsValid(
                toolCall.OperationAdmission,
                toolCall.Safety,
                toolCall.NyxIdProvenance))
        {
            return FailConditionGuard(
                state,
                operationKey,
                guardedStep,
                ToolAdmissionInvalid,
                ToolAdmissionInvalidMessage,
                now);
        }

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);
        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var materialized = next.ActiveTask.Steps.Single(step =>
            string.Equals(step.StepId, guardedStep.StepId, StringComparison.Ordinal));
        MaterializeGuardedToolStep(next, materialized, toolCall, now);
        var verificationStep = BuildVerificationStep(next, materialized, now);
        next.ActiveTask.Steps.Add(verificationStep);
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            [verificationStep]);

        ActivateStep(next, materialized, now);
        FinalizeDerivedState(next, now);

        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            new NyxIdChatOperationDispatchCommand
            {
                Key = materialized.Operation.Key.Clone(),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = toolCall.CallId,
                    ToolName = toolCall.ToolName,
                    ArgumentsJson = toolCall.ArgumentsJson,
                    MayChangeExternalState = toolCall.Safety.MayChangeExternalState,
                    Idempotent = !toolCall.Safety.MayChangeExternalState,
                    IdempotencyKey = materialized.Operation.IdempotencyKey,
                    OperationAdmission = toolCall.OperationAdmission?.Clone(),
                    Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                        toolCall.Presentation,
                        toolCall.ToolName),
                },
            });
    }

    private static void MaterializeGuardedToolStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        NyxIdChatToolCall call,
        Timestamp now)
    {
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            state.ActiveTurn.TurnId,
            state.ActiveTask.TaskId,
            step.StepId,
            "1");
        var source = new NyxIdChatToolStepSource
        {
            ToolName = call.ToolName,
            OperationAdmission = call.OperationAdmission?.Clone(),
            Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                call.Presentation,
                call.ToolName),
        };
        if (call.OperationAdmission is { } admission)
        {
            source.ServiceSlug = admission.ServiceSlug;
            source.ServiceId = admission.ServiceInstanceId;
        }
        if (call.NyxIdProvenance is { } provenance)
        {
            if (call.OperationAdmission is null)
            {
                source.ServiceSlug = provenance.ServiceSlug;
                source.ServiceId = provenance.ConnectedServiceId;
            }
            if (provenance.HasReadinessCapabilityId)
                source.ReadinessCapabilityId = provenance.ReadinessCapabilityId;
        }
        if (TryBuildAuthorizationReadinessInput(call, out var authorizationReadiness))
            source.AuthorizationReadiness = authorizationReadiness;

        step.Source = new NyxIdChatStepSource { Tool = source };
        step.MayChangeExternalState = call.Safety.MayChangeExternalState;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        step.Status = NyxIdChatStepStatus.Running;
        step.Operation = new NyxIdChatOperationState
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = state.ConversationActorId,
                TurnId = state.ActiveTurn.TurnId,
                TaskId = state.ActiveTask.TaskId,
                StepId = step.StepId,
                OperationId = operationId,
                OperationGeneration = 1,
            },
            Kind = NyxIdChatStepKind.Tool,
            Phase = NyxIdChatOperationPhase.Requested,
            MayChangeExternalState = call.Safety.MayChangeExternalState,
            Idempotent = !call.Safety.MayChangeExternalState,
            IdempotencyKey = operationId,
            RequestedAt = now.Clone(),
        };
        step.Estimate = new NyxIdChatStepEstimate
        {
            Kind = NyxIdChatStepEstimateKind.Duration,
            Seconds = DefaultToolEstimateSeconds,
        };
        if (call.OperationAdmission is not null &&
            TryParseArguments(call.ArgumentsJson, out var arguments))
        {
            step.RetryInputRebuildable = true;
            step.RetryToolInput = new NyxIdChatRetryToolInputState
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Arguments = arguments,
                OperationAdmission = call.OperationAdmission.Clone(),
                Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                    call.Presentation,
                    call.ToolName),
            };
        }
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
    }

    private static NyxIdChatTaskStepState BuildSkippedGuardedVerificationStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState guardedStep,
        int order,
        Timestamp now)
    {
        var step = new NyxIdChatTaskStepState
        {
            StepId = BuildStableIdentity(
                "step",
                state.ConversationActorId,
                state.ActiveTurn.TurnId,
                state.ActiveTask.TaskId,
                guardedStep.StepId,
                order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "guarded-tool-verification-skipped"),
            Order = order,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Skipped,
            Required = true,
            Description = "Skip read-back because the guarded external effect was not applied.",
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    EffectStepId = guardedStep.StepId,
                    Check = "guarded_effect_not_applied",
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { guardedStep.StepId },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static bool TryResolveConditionGuard(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState currentStep,
        out NyxIdChatTaskStepState conditionStep,
        out NyxIdChatTaskStepState guardedStep)
    {
        conditionStep = null!;
        guardedStep = null!;
        if (currentStep.Kind != NyxIdChatStepKind.Llm || currentStep.DependsOn.Count != 1)
            return false;
        conditionStep = state.ActiveTask.Steps.SingleOrDefault(step =>
            string.Equals(step.StepId, currentStep.DependsOn[0], StringComparison.Ordinal) &&
            step.Kind == NyxIdChatStepKind.Condition &&
            step.Status == NyxIdChatStepStatus.Done &&
            step.Source?.Condition?.Condition is not null)!;
        if (conditionStep is null)
            return false;
        var conditionStepId = conditionStep.StepId;
        guardedStep = state.ActiveTask.Steps.SingleOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            string.Equals(step.Guard?.ConditionStepId, conditionStepId,
                StringComparison.Ordinal) &&
            step.Guard?.RequiredOutcome == NyxIdChatConditionOutcome.True)!;
        return guardedStep is not null;
    }

    private static NyxIdChatTaskLifecycleDecision FailConditionGuard(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        NyxIdChatTaskStepState guardedStep,
        string failureCode,
        string safeMessage,
        Timestamp now)
    {
        var decision = FailClosed(state, key, failureCode, safeMessage, now);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return decision;
        var next = decision.State.Clone();
        var guarded = next.ActiveTask.Steps.Single(step =>
            string.Equals(step.StepId, guardedStep.StepId, StringComparison.Ordinal));
        if (guarded.Status == NyxIdChatStepStatus.Planned)
        {
            guarded.Status = NyxIdChatStepStatus.Cancelled;
            guarded.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
            guarded.FailureCode = failureCode;
            guarded.SafeMessage = safeMessage;
            guarded.UpdatedAt = now.Clone();
            guarded.AvailableActions =
                NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(guarded);
        }
        NyxIdChatTaskTransitionPolicy.RefreshTaskOutcome(next);
        FinalizeDerivedState(next, now);
        return decision with { State = next };
    }

    private static NyxIdChatTaskLifecycleDecision ApplyLlmInputPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatToolCall toolCall,
        Timestamp now)
    {
        var requestId = BuildStableIdentity(
            "input-request",
            state.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            currentStep.StepId,
            toolCall.CallId);
        if (!NyxIdChatAskUserContract.TryParse(requestId, toolCall.ArgumentsJson, out var input))
        {
            return FailClosed(
                state,
                operationKey,
                InputRequestInvalid,
                InputRequestInvalidMessage,
                now);
        }

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var order = next.ActiveTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            currentStep.StepId,
            toolCall.CallId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "input");
        var inputStep = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Description = input.Prompt,
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = requestId },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { currentStep.StepId },
            UpdatedAt = now.Clone(),
        };
        inputStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(inputStep);
        next.ActiveTask.Steps.Add(inputStep);
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            [inputStep]);
        next.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        next.ActiveTask.ActiveStepId = stepId;
        next.ActiveTask.ActiveOperationId = string.Empty;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        next.ActiveTurn.TerminalAt = null;

        var request = new NyxIdChatInputRequestCommand
        {
            ScopeId = next.ScopeId,
            ConversationActorId = next.ConversationActorId,
            TurnId = operationKey.TurnId,
            TaskId = operationKey.TaskId,
            StepId = stepId,
            RequestId = requestId,
            ToolCallId = toolCall.CallId,
            Prompt = input.Prompt,
            AllowFreeText = input.AllowFreeText,
            MultiSelect = input.MultiSelect,
        };
        if (input.NumericThreshold is not null)
            request.NumericThreshold = input.NumericThreshold.Clone();
        request.Options.AddRange(input.Options.Select(static option => option.Clone()));
        next.PendingInputRequest = request.Clone();
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            NextCommand: null,
            InputRequest: request);
    }

    private static NyxIdChatTaskLifecycleDecision FailClosed(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        string failureCode,
        string safeMessage,
        Timestamp now)
    {
        var failure = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = failureCode,
                SafeMessage = safeMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };
        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, failure);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, key, now);
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            failureCode,
            safeMessage,
            next,
            NextCommand: null);
    }

    private static NyxIdChatOperationResultSignal NormalizeToolResult(
        NyxIdChatOperationResultSignal signal,
        NyxIdChatTaskStepState currentStep)
    {
        if (signal.ResultCase != NyxIdChatOperationResultSignal.ResultOneofCase.Tool)
        {
            return signal;
        }

        var receipt = signal.Tool.Receipt;
        if (receipt?.Status == AgentToolReceiptStatus.Success)
        {
            if (!currentStep.MayChangeExternalState &&
                !currentStep.Operation.MayChangeExternalState)
            {
                return signal;
            }

            var normalized = signal.Clone();
            normalized.Tool.ExternalEffect = receipt.MutationStage ==
                                             AgentToolReceiptMutationStage.ReadModelObserved
                ? NyxIdChatEffectEvidence.Confirmed
                : NyxIdChatEffectEvidence.MayHaveChanged;
            return normalized;
        }

        if (signal.Tool.ExternalEffect != NyxIdChatEffectEvidence.MayHaveChanged)
            return signal;

        return new NyxIdChatOperationResultSignal
        {
            Key = signal.Key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = string.IsNullOrWhiteSpace(receipt?.ErrorCode)
                    ? "NYXID_CHAT_TOOL_OUTCOME_UNCERTAIN"
                    : receipt.ErrorCode,
                SafeMessage = string.IsNullOrWhiteSpace(receipt?.ErrorMessage)
                    ? "The external tool outcome could not be confirmed."
                    : receipt.ErrorMessage,
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        };
    }

    private static NyxIdChatTaskStepState BuildToolStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState sourceStep,
        NyxIdChatToolCall call,
        Timestamp now)
    {
        var executionTurnId = sourceStep.Operation.Key.TurnId;
        var executionTaskId = sourceStep.Operation.Key.TaskId;
        var order = state.ActiveTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            executionTurnId,
            executionTaskId,
            sourceStep.StepId,
            call.CallId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "tool");
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            executionTurnId,
            executionTaskId,
            stepId,
            "1");
        var operationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = executionTurnId,
            TaskId = executionTaskId,
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };
        var toolSource = new NyxIdChatToolStepSource
        {
            ToolName = call.ToolName,
            OperationAdmission = call.OperationAdmission?.Clone(),
            Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                call.Presentation,
                call.ToolName),
        };
        if (call.OperationAdmission is { } admission)
        {
            toolSource.ServiceSlug = admission.ServiceSlug;
            toolSource.ServiceId = admission.ServiceInstanceId;
        }
        if (call.NyxIdProvenance is { } provenance)
        {
            if (call.OperationAdmission is null)
            {
                toolSource.ServiceSlug = provenance.ServiceSlug;
                toolSource.ServiceId = provenance.ConnectedServiceId;
            }
            if (provenance.HasReadinessCapabilityId &&
                !string.IsNullOrWhiteSpace(provenance.ReadinessCapabilityId))
            {
                toolSource.ReadinessCapabilityId = provenance.ReadinessCapabilityId;
            }
        }
        if (TryBuildAuthorizationReadinessInput(call, out var authorizationReadiness))
            toolSource.AuthorizationReadiness = authorizationReadiness;

        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = $"Run authorized tool {call.ToolName}.",
            Source = new NyxIdChatStepSource
            {
                Tool = toolSource,
            },
            MayChangeExternalState = call.Safety.MayChangeExternalState,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { sourceStep.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = operationKey,
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Requested,
                MayChangeExternalState = call.Safety.MayChangeExternalState,
                Idempotent = !call.Safety.MayChangeExternalState,
                IdempotencyKey = operationId,
                RequestedAt = now.Clone(),
            },
            Estimate = new NyxIdChatStepEstimate
            {
                Kind = NyxIdChatStepEstimateKind.Duration,
                Seconds = DefaultToolEstimateSeconds,
            },
            UpdatedAt = now.Clone(),
        };
        if (call.OperationAdmission is not null &&
            TryParseArguments(call.ArgumentsJson, out var arguments))
        {
            step.RetryInputRebuildable = true;
            step.RetryToolInput = new NyxIdChatRetryToolInputState
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Arguments = arguments,
                OperationAdmission = call.OperationAdmission?.Clone(),
                Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                    call.Presentation,
                    call.ToolName),
            };
        }
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static NyxIdChatTaskStepState BuildVerificationStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState toolStep,
        Timestamp now,
        bool failureRecovery = false)
    {
        var order = state.ActiveTask.Steps.Max(static step => step.Order) + 1;
        var requiresReadBack = toolStep.MayChangeExternalState;
        var readBack = toolStep.Source?.Tool?.OperationAdmission?.ReadBack;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            toolStep.Operation.Key.TurnId,
            toolStep.Operation.Key.TaskId,
            toolStep.StepId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requiresReadBack
                ? failureRecovery ? "tool-reconciliation" : "tool-verification"
                : "llm-continuation");
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            toolStep.Operation.Key.TurnId,
            toolStep.Operation.Key.TaskId,
            stepId,
            "1");
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = toolStep.Operation.Key.TurnId,
            TaskId = toolStep.Operation.Key.TaskId,
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = requiresReadBack ? NyxIdChatStepKind.Postcondition : NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = requiresReadBack
                ? failureRecovery
                    ? "Reconcile the uncertain external effect through its admitted read-back."
                    : "Verify the external effect through its admitted read-back."
                : "Communicate the typed read result.",
            Source = requiresReadBack
                ? new NyxIdChatStepSource
                {
                    Postcondition = new NyxIdChatPostconditionStepSource
                    {
                        EffectStepId = toolStep.StepId,
                        Check = readBack?.CheckName ?? "verification_unavailable",
                        ToolReadBack = readBack?.Clone(),
                        ProviderResourceId = toolStep.Source?.Tool?.ProviderResourceId ?? string.Empty,
                    },
                }
                : new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { toolStep.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = requiresReadBack ? NyxIdChatStepKind.Postcondition : NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            Estimate = new NyxIdChatStepEstimate
            {
                Kind = NyxIdChatStepEstimateKind.Duration,
                Seconds = DefaultVerificationEstimateSeconds,
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static NyxIdChatOperationDispatchCommand? ActivatePlannedVerificationStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey completedToolKey,
        Timestamp now,
        bool mutationReadModelObserved = false)
    {
        var step = state.ActiveTask.Steps.SingleOrDefault(candidate =>
            candidate.Kind is (NyxIdChatStepKind.Llm or NyxIdChatStepKind.Postcondition) &&
            candidate.Status == NyxIdChatStepStatus.Planned &&
            candidate.DependsOn.Count == 1 &&
            string.Equals(candidate.DependsOn[0], completedToolKey.StepId, StringComparison.Ordinal));
        if (step?.Operation?.Key is null)
            return null;

        if (mutationReadModelObserved)
        {
            step.Kind = NyxIdChatStepKind.Llm;
            step.Description = "Communicate the typed mutation result observed from its canonical read model.";
            step.Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() };
            step.Operation.Kind = NyxIdChatStepKind.Llm;
        }

        else if (TryBuildServiceConnectedPostconditionDispatch(
                     state,
                     step,
                     completedToolKey,
                     now) is { } serviceConnectedPostcondition)
        {
            return serviceConnectedPostcondition;
        }

        var readBack = step.Source?.Postcondition?.ToolReadBack;
        if (step.Kind == NyxIdChatStepKind.Postcondition &&
            !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(readBack))
        {
            step.Status = NyxIdChatStepStatus.Uncertain;
            step.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
            step.FailureCode = NyxIdChatToolVerificationPort.UnavailableCode;
            step.SafeMessage = "No admitted verification read is available for this effect.";
            step.Operation.Phase = NyxIdChatOperationPhase.Uncertain;
            step.Operation.CompletedAt = now.Clone();
            step.UpdatedAt = now.Clone();
            var effectStep = state.ActiveTask.Steps.First(candidate =>
                string.Equals(candidate.StepId, completedToolKey.StepId, StringComparison.Ordinal));
            effectStep.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
            effectStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(effectStep);
            NyxIdChatTaskTransitionPolicy.RefreshTaskOutcome(state);
            return null;
        }

        ActivateStep(state, step, now);
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = step.Operation.Key.Clone(),
        };
        if (step.Kind == NyxIdChatStepKind.Postcondition)
        {
            command.ToolVerification = new NyxIdChatToolVerificationInput
            {
                EffectStepId = completedToolKey.StepId,
                ReadBack = readBack!.Clone(),
                ProviderResourceId = step.Source!.Postcondition.ProviderResourceId,
            };
        }
        else
        {
            command.Llm = new NyxIdChatLLMOperationInput
            {
                ContinueSession = true,
                Intent = state.ActiveTurn.Intent,
            };
        }
        return command;
    }

    private static NyxIdChatOperationDispatchCommand? TryBuildServiceConnectedPostconditionDispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState continuationStep,
        NyxIdChatOperationKey completedToolKey,
        Timestamp now)
    {
        var toolStep = state.ActiveTask.Steps.SingleOrDefault(candidate =>
            string.Equals(candidate.StepId, completedToolKey.StepId, StringComparison.Ordinal));
        var serviceConnect = toolStep?.Source?.Tool?.ServiceConnectPostcondition;
        var providerResourceId = toolStep?.Source?.Tool?.ProviderResourceId?.Trim();
        if (!IsServiceConnectIntent(state) ||
            !string.Equals(
                toolStep?.Source?.Tool?.ToolName,
                NyxIdRequireServiceToolName,
                StringComparison.Ordinal) ||
            serviceConnect is null ||
            string.IsNullOrWhiteSpace(providerResourceId))
        {
            return null;
        }

        var actionRequestId = BuildStableIdentity(
            "action-postcondition",
            state.ConversationActorId,
            completedToolKey.TurnId,
            completedToolKey.TaskId,
            completedToolKey.StepId,
            providerResourceId);
        continuationStep.Required = false;
        continuationStep.Status = NyxIdChatStepStatus.Cancelled;
        continuationStep.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        continuationStep.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
        continuationStep.Operation.CompletedAt = now.Clone();
        continuationStep.UpdatedAt = now.Clone();
        continuationStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(continuationStep);

        var order = state.ActiveTask.Steps.Max(static candidate => candidate.Order) + 1;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            completedToolKey.TurnId,
            completedToolKey.TaskId,
            completedToolKey.StepId,
            providerResourceId,
            "service-connected-postcondition");
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            completedToolKey.TurnId,
            completedToolKey.TaskId,
            stepId,
            "1");
        var postconditionStep = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Verify the exact connected service from its typed read model.",
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = actionRequestId,
                    EffectStepId = completedToolKey.StepId,
                    Check = ServiceConnectedCheck,
                    ProviderResourceId = providerResourceId,
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { completedToolKey.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = state.ConversationActorId,
                    TurnId = completedToolKey.TurnId,
                    TaskId = completedToolKey.TaskId,
                    StepId = stepId,
                    OperationId = operationId,
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            Estimate = new NyxIdChatStepEstimate
            {
                Kind = NyxIdChatStepEstimateKind.Duration,
                Seconds = DefaultVerificationEstimateSeconds,
            },
            UpdatedAt = now.Clone(),
        };
        postconditionStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(postconditionStep);
        var input = new NyxIdChatActionPostconditionInput
        {
            ScopeId = state.ScopeId,
            OwnerSubject = state.OwnerSubject,
            OriginTurnId = completedToolKey.TurnId,
            ActionRequestId = actionRequestId,
            Action = NyxIdAssistantActionKind.ServiceConnect,
            ReportedDisposition = NyxIdChatActionDisposition.Completed,
            ResourceHint = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = providerResourceId,
                },
            },
            Params = new NyxIdAssistantActionParams
            {
                CatalogServiceConnect = serviceConnect.Clone(),
            },
        };

        state.ActiveTask.Steps.Add(postconditionStep);
        NyxIdChatPlanRevisions.CommitChange(
            state.ActiveTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            [postconditionStep],
            [continuationStep]);
        ActivateStep(state, postconditionStep, now);
        return new NyxIdChatOperationDispatchCommand
        {
            Key = postconditionStep.Operation.Key.Clone(),
            ActionPostcondition = input,
        };
    }

    private static bool IsServiceConnectIntent(NyxIdChatConversationGAgentState state) =>
        state.ActiveTurn?.Intent == NyxIdChatTurnIntent.ServiceConnect;

    private static bool IsServiceConnectTool(string? toolName) =>
        string.Equals(toolName, NyxIdCatalogToolName, StringComparison.Ordinal) ||
        string.Equals(toolName, NyxIdRequireServiceToolName, StringComparison.Ordinal);

    private static bool HasCompletedCatalogStep(NyxIdChatConversationGAgentState state) =>
        state.ActiveTask?.Steps.Any(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Status == NyxIdChatStepStatus.Done &&
            string.Equals(
                step.Source?.Tool?.ToolName,
                NyxIdCatalogToolName,
                StringComparison.Ordinal)) == true;

    private static bool HasVerifiedServiceConnectedPostcondition(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask?.Steps.Any(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.Status == NyxIdChatStepStatus.Done &&
            step.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
            string.Equals(
                step.Source?.Postcondition?.Check,
                ServiceConnectedCheck,
                StringComparison.Ordinal)) == true;

    private static bool IsServiceConnectedPostcondition(NyxIdChatTaskStepState step) =>
        step.Kind == NyxIdChatStepKind.Postcondition &&
        string.Equals(
            step.Source?.Postcondition?.Check,
            ServiceConnectedCheck,
            StringComparison.Ordinal);

    private static bool MatchesServiceConnectedPostcondition(
        NyxIdChatTaskStepState step,
        NyxIdChatActionPostconditionResult result)
    {
        var frozen = step.Source?.Postcondition;
        if (frozen is null ||
            string.IsNullOrWhiteSpace(frozen.ActionRequestId) ||
            string.IsNullOrWhiteSpace(frozen.ProviderResourceId) ||
            !string.Equals(
                frozen.ActionRequestId,
                result.ActionRequestId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var reportedUserServiceId = result.Resource?.UserService?.UserServiceId?.Trim();
        if (!string.IsNullOrWhiteSpace(reportedUserServiceId) &&
            !string.Equals(
                frozen.ProviderResourceId,
                reportedUserServiceId,
                StringComparison.Ordinal))
        {
            return false;
        }

        return !result.Verified ||
               result.Disposition != NyxIdChatActionDisposition.Completed ||
               string.Equals(
                   frozen.ProviderResourceId,
                   reportedUserServiceId,
                   StringComparison.Ordinal);
    }

    private static bool TryParseServiceConnectPostcondition(
        string? argumentsJson,
        out NyxIdCatalogServiceConnectParams postcondition)
    {
        postcondition = new NyxIdCatalogServiceConnectParams();
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("service_slug", out var slugElement) ||
                slugElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("requested_scopes", out var scopesElement) ||
                scopesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var serviceSlug = slugElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(serviceSlug))
                return false;

            postcondition.ServiceSlug = serviceSlug;
            foreach (var scopeElement in scopesElement.EnumerateArray())
            {
                if (scopeElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(scopeElement.GetString()))
                {
                    return false;
                }

                postcondition.RequestedScopes.Add(scopeElement.GetString()!.Trim());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void BindProviderResourceIdentity(
        NyxIdChatConversationGAgentState state,
        string effectStepId,
        string providerResourceId)
    {
        var effectStep = state.ActiveTask?.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, effectStepId, StringComparison.Ordinal));
        if (effectStep?.Source?.Tool is null)
            return;

        effectStep.Source.Tool.ProviderResourceId = providerResourceId?.Trim() ?? string.Empty;
        var verification = state.ActiveTask!.Steps.FirstOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.DependsOn.Count == 1 &&
            string.Equals(step.DependsOn[0], effectStepId, StringComparison.Ordinal));
        if (verification?.Source?.Postcondition is not null)
            verification.Source.Postcondition.ProviderResourceId = effectStep.Source.Tool.ProviderResourceId;
    }

    private static NyxIdChatOperationDispatchCommand? ReplanFailureRecoveryVerificationStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey effectKey,
        Timestamp now)
    {
        var effectStep = FindCurrentStep(state, effectKey);
        var planned = state.ActiveTask.Steps.SingleOrDefault(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition &&
            candidate.Status == NyxIdChatStepStatus.Planned &&
            candidate.DependsOn.Contains(effectKey.StepId));
        if (effectStep is null || planned?.Operation?.Key is null ||
            !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(
                planned.Source?.Postcondition?.ToolReadBack))
        {
            return ActivatePlannedVerificationStep(state, effectKey, now);
        }

        planned.Required = false;
        planned.Status = NyxIdChatStepStatus.Cancelled;
        planned.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        planned.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
        planned.Operation.CompletedAt = now.Clone();
        planned.UpdatedAt = now.Clone();
        planned.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(planned);

        var reconciliation = BuildVerificationStep(state, effectStep, now, failureRecovery: true);
        state.ActiveTask.Steps.Add(reconciliation);
        NyxIdChatPlanRevisions.CommitChange(
            state.ActiveTask,
            NyxIdChatPlanRevisionCause.FailureRecovery,
            now,
            [reconciliation],
            [planned]);
        return ActivatePlannedVerificationStep(state, effectKey, now);
    }

    internal static NyxIdChatOperationDispatchCommand? PlanFencedEffectVerification(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey effectKey,
        Timestamp now)
    {
        var effectStep = FindCurrentStep(state, effectKey);
        var readBack = effectStep?.Source?.Tool?.OperationAdmission?.ReadBack;
        if (effectStep is null ||
            !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(readBack))
        {
            return null;
        }

        var verification = BuildVerificationStep(state, effectStep, now, failureRecovery: true);
        verification.Status = NyxIdChatStepStatus.Running;
        verification.Operation.Phase = NyxIdChatOperationPhase.Requested;
        state.ActiveTask.Steps.Add(verification);
        NyxIdChatPlanRevisions.CommitChange(
            state.ActiveTask,
            NyxIdChatPlanRevisionCause.FailureRecovery,
            now,
            [verification]);
        return new NyxIdChatOperationDispatchCommand
        {
            Key = verification.Operation.Key.Clone(),
            ToolVerification = new NyxIdChatToolVerificationInput
            {
                EffectStepId = effectStep.StepId,
                ReadBack = readBack!.Clone(),
                ProviderResourceId = effectStep.Source.Tool.ProviderResourceId,
            },
        };
    }

    private static void ApplyVerificationEvidence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatToolVerificationResult verification,
        Timestamp now,
        bool refreshTaskOutcome = true)
    {
        var effectStep = state.ActiveTask.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, verification.EffectStepId, StringComparison.Ordinal));
        if (effectStep is null)
            return;

        switch (verification.Disposition)
        {
            case NyxIdChatToolVerificationDisposition.Applied:
                effectStep.Status = NyxIdChatStepStatus.Done;
                effectStep.ExternalEffect = NyxIdChatEffectEvidence.Confirmed;
                break;
            case NyxIdChatToolVerificationDisposition.NotApplied:
                effectStep.Status = NyxIdChatStepStatus.Failed;
                effectStep.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
                effectStep.FailureCode = string.IsNullOrWhiteSpace(verification.FailureCode)
                    ? "NYXID_CHAT_EFFECT_NOT_APPLIED"
                    : verification.FailureCode;
                effectStep.SafeMessage = string.IsNullOrWhiteSpace(verification.SafeMessage)
                    ? "The verification read proved that the effect was not applied."
                    : verification.SafeMessage;
                break;
            default:
                effectStep.Status = NyxIdChatStepStatus.Uncertain;
                effectStep.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                effectStep.FailureCode = verification.FailureCode;
                effectStep.SafeMessage = verification.SafeMessage;
                break;
        }
        effectStep.UpdatedAt = now.Clone();
        effectStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(effectStep);
        if (refreshTaskOutcome)
            NyxIdChatTaskTransitionPolicy.RefreshTaskOutcome(state);
    }

    private static NyxIdChatTaskLifecycleDecision ApplyRecoveredToolVerification(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState effectStep,
        NyxIdChatToolVerificationResult verification,
        Timestamp now)
    {
        var frozenReadBack = effectStep.Source?.Tool?.OperationAdmission?.ReadBack;
        if (frozenReadBack?.ReadOperation is null ||
            !string.Equals(effectStep.StepId, verification.EffectStepId, StringComparison.Ordinal) ||
            !frozenReadBack.ReadOperation.Equals(verification.ReadOperation) ||
            !string.Equals(frozenReadBack.CheckName, verification.CheckName, StringComparison.Ordinal))
        {
            return new NyxIdChatTaskLifecycleDecision(
                NyxIdChatTransitionOutcome.Rejected,
                ToolVerificationEvidenceMismatch,
                "The recovery evidence did not match the frozen effect admission.",
                state.Clone(),
                NextCommand: null);
        }

        var next = state.Clone();
        var nextEffect = next.ActiveTask.Steps.Single(step =>
            string.Equals(step.StepId, effectStep.StepId, StringComparison.Ordinal));
        var superseded = next.ActiveTask.Steps.SingleOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.Status == NyxIdChatStepStatus.Planned &&
            step.DependsOn.Contains(effectStep.StepId));
        if (superseded is not null)
        {
            superseded.Required = false;
            superseded.Status = NyxIdChatStepStatus.Cancelled;
            superseded.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
            superseded.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
            superseded.Operation.CompletedAt = now.Clone();
            superseded.UpdatedAt = now.Clone();
            superseded.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(superseded);
        }

        var reconciliation = BuildVerificationStep(next, nextEffect, now, failureRecovery: true);
        reconciliation.Status = verification.Disposition ==
                                NyxIdChatToolVerificationDisposition.Unavailable
            ? NyxIdChatStepStatus.Uncertain
            : NyxIdChatStepStatus.Done;
        reconciliation.ExternalEffect = verification.Disposition switch
        {
            NyxIdChatToolVerificationDisposition.Applied => NyxIdChatEffectEvidence.Confirmed,
            NyxIdChatToolVerificationDisposition.NotApplied => NyxIdChatEffectEvidence.NotApplied,
            _ => NyxIdChatEffectEvidence.MayHaveChanged,
        };
        reconciliation.FailureCode = verification.FailureCode;
        reconciliation.SafeMessage = verification.SafeMessage;
        reconciliation.Operation.Phase = verification.Disposition ==
                                         NyxIdChatToolVerificationDisposition.Unavailable
            ? NyxIdChatOperationPhase.Uncertain
            : NyxIdChatOperationPhase.Succeeded;
        reconciliation.Operation.CompletedAt = now.Clone();
        reconciliation.UpdatedAt = now.Clone();
        reconciliation.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(reconciliation);
        next.ActiveTask.Steps.Add(reconciliation);
        NyxIdChatPlanRevisions.CommitChange(
            next.ActiveTask,
            NyxIdChatPlanRevisionCause.FailureRecovery,
            now,
            [reconciliation],
            superseded is null ? [] : [superseded]);

        ApplyVerificationEvidence(next, verification, now, refreshTaskOutcome: false);
        next.ActiveTask.ActiveStepId = string.Empty;
        next.ActiveTask.ActiveOperationId = string.Empty;
        NyxIdChatTaskTransitionPolicy.RefreshTaskOutcome(next);
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            NyxIdChatTransitionOutcome.Accepted,
            NyxIdChatTaskTransitionPolicy.OperationSucceeded,
            string.Empty,
            next,
            NextCommand: null);
    }

    private static bool MatchesFrozenVerification(
        NyxIdChatTaskStepState verificationStep,
        NyxIdChatToolVerificationResult verification)
    {
        var frozen = verificationStep.Source?.Postcondition;
        return verificationStep.Kind == NyxIdChatStepKind.Postcondition &&
               frozen?.ToolReadBack?.ReadOperation is not null &&
               !string.IsNullOrWhiteSpace(frozen.EffectStepId) &&
               string.Equals(
                   frozen.EffectStepId,
                   verification.EffectStepId,
                   StringComparison.Ordinal) &&
               frozen.ToolReadBack.ReadOperation.Equals(verification.ReadOperation) &&
               string.Equals(
                   frozen.ToolReadBack.CheckName,
                   verification.CheckName,
                   StringComparison.Ordinal);
    }

    private static bool TryParseArguments(string argumentsJson, out Struct arguments)
    {
        try
        {
            arguments = JsonParser.Default.Parse<Struct>(argumentsJson);
            return true;
        }
        catch (InvalidJsonException)
        {
            arguments = new Struct();
            return false;
        }
    }

    private static bool TryBuildAuthorizationReadinessInput(
        NyxIdChatToolCall call,
        out NyxIdChatAuthorizationReadinessInput input)
    {
        input = new NyxIdChatAuthorizationReadinessInput();
        if (!string.Equals(
                call.ToolName,
                NyxIdRequireServiceToolName,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("service_slug", out var slugElement) ||
                slugElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("requested_scopes", out var scopesElement) ||
                scopesElement.ValueKind != JsonValueKind.Array ||
                scopesElement.GetArrayLength() > 64)
            {
                return false;
            }

            var serviceSlug = NormalizeAuthorizationReadinessSlug(slugElement.GetString());
            if (serviceSlug is null)
                return false;

            var parameters = new NyxIdChatRequireServiceParams
            {
                ServiceSlug = serviceSlug,
            };
            foreach (var scopeElement in scopesElement.EnumerateArray())
            {
                if (scopeElement.ValueKind != JsonValueKind.String)
                    return false;

                var scope = scopeElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(scope) ||
                    scope.Length > 256 ||
                    scope.Any(char.IsControl))
                {
                    return false;
                }

                if (!parameters.RequestedScopes.Contains(scope, StringComparer.Ordinal))
                    parameters.RequestedScopes.Add(scope);
            }

            if (!TryReadOptionalAuthorizationReadinessString(
                    root,
                    "service_label",
                    80,
                    out var serviceLabel) ||
                !TryReadOptionalAuthorizationReadinessString(
                    root,
                    "resource_uri",
                    256,
                    out var resourceUri))
            {
                return false;
            }

            parameters.ServiceLabel = serviceLabel;
            parameters.ResourceUri = resourceUri;
            input = new NyxIdChatAuthorizationReadinessInput
            {
                ToolName = NyxIdRequireServiceToolName,
                Params = parameters,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeAuthorizationReadinessSlug(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 100 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? normalized
            : null;
    }

    private static bool TryReadOptionalAuthorizationReadinessString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
            return false;

        var normalized = element.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
            return false;

        value = normalized;
        return true;
    }

    private static void CancelPlannedVerificationStep(
        NyxIdChatConversationGAgentState state,
        string toolStepId,
        Timestamp now)
    {
        var step = state.ActiveTask.Steps.SingleOrDefault(candidate =>
            candidate.Kind is (NyxIdChatStepKind.Llm or NyxIdChatStepKind.Postcondition) &&
            candidate.Status == NyxIdChatStepStatus.Planned &&
            candidate.DependsOn.Contains(toolStepId));
        if (step is null)
            return;

        step.Status = NyxIdChatStepStatus.Cancelled;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        step.UpdatedAt = now.Clone();
        if (step.Operation is not null)
            step.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
    }

    private static void ApplyApprovalObservation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatTaskStepState previousStep,
        Timestamp now)
    {
        var receipt = signal.Tool?.Receipt;
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.ApprovalRequestId))
        {
            if (state.ActiveTask.Status != NyxIdChatTaskStatus.Active)
                state.PendingApproval = null;
            return;
        }

        var step = FindCurrentStep(state, signal.Key);
        if (step is null)
            return;

        step.ApprovalRequestId = receipt.ApprovalRequestId;
        step.UpdatedAt = now.Clone();
        if (step.Source?.Tool?.OperationAdmission is not null &&
            receipt.ExactServiceApproval is null &&
            (receipt.Status is AgentToolReceiptStatus.ApprovalRequired or
                AgentToolReceiptStatus.Denied))
        {
            step.ApprovalObservation = new NyxIdChatPostReturnApprovalObservation
            {
                ApprovalRequestId = receipt.ApprovalRequestId,
                DecisionMode = receipt.NyxIdApprovalDecisionMode,
                ReceiptStatus = receipt.Status,
                ObservedAt = now.Clone(),
                TerminalOutcome = receipt.NyxIdApprovalTerminalOutcome,
                SubjectKind = receipt.SubjectKind,
                SubjectId = receipt.SubjectId,
            };
            state.PendingApproval = null;
            return;
        }
        if (receipt.Status != AgentToolReceiptStatus.ApprovalRequired)
            return;

        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = string.Empty;
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = receipt.ApprovalRequestId,
            TurnId = signal.Key.TurnId,
            TaskId = signal.Key.TaskId,
            StepId = signal.Key.StepId,
            ToolCallId = receipt.CallId,
            ToolName = string.IsNullOrWhiteSpace(receipt.ToolName)
                ? previousStep.Source?.Tool?.ToolName ?? string.Empty
                : receipt.ToolName,
            AskedAt = now.Clone(),
            ExpiresAt = receipt.ExactServiceApproval?.ExpiresAt?.Clone() ??
                        Timestamp.FromDateTimeOffset(
                            now.ToDateTimeOffset() + ToolApprovalExpiryWindow),
            Presentation = new NyxIdChatApprovalPresentation
            {
                Action = string.IsNullOrWhiteSpace(receipt.SideEffectKind)
                    ? receipt.ToolName ?? string.Empty
                    : receipt.SideEffectKind,
                Target = ResolveApprovalTarget(receipt),
                ActorLabel = NyxIdChatServiceDefaults.DisplayName,
                Reversibility = receipt.IsDestructive
                    ? NyxIdChatApprovalReversibility.Irreversible
                    : NyxIdChatApprovalReversibility.Unknown,
                GrantBoundary = "within_grant",
            },
        };
        if (receipt.ExactServiceApproval is not null)
        {
            state.PendingApproval.ExactServiceApproval =
                receipt.ExactServiceApproval.Clone();
            state.PendingApproval.Presentation.NyxidRequestId =
                receipt.ExactServiceApproval.RequestId;
        }
    }

    private static void ApplyConnectedServiceApprovalReentry(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatTaskStepState previousStep,
        Timestamp now)
    {
        var receipt = signal.Tool?.Receipt;
        if (previousStep.Source?.Tool?.OperationAdmission is null ||
            receipt?.ExactServiceApproval is not null ||
            receipt?.Status != AgentToolReceiptStatus.ApprovalRequired)
        {
            return;
        }

        var step = FindCurrentStep(state, signal.Key);
        if (step?.Operation is null)
            return;

        step.Status = NyxIdChatStepStatus.Failed;
        step.Operation.Phase = NyxIdChatOperationPhase.Failed;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        step.FailureCode = receipt.ErrorCode;
        step.SafeMessage = receipt.ErrorMessage;
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        step.UpdatedAt = now.Clone();
        state.ActiveTask.ActiveStepId = string.Empty;
        state.ActiveTask.ActiveOperationId = string.Empty;
    }

    private static string ResolveApprovalTarget(AgentToolReceipt receipt)
    {
        var kind = receipt.SubjectKind?.Trim() ?? string.Empty;
        var id = receipt.SubjectId?.Trim() ?? string.Empty;
        return (kind, id) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{kind}:{id}",
            (_, { Length: > 0 }) => id,
            ({ Length: > 0 }, _) => kind,
            _ => receipt.ToolName?.Trim() ?? string.Empty,
        };
    }

    // Expiry is a denial: the exact waiting tool step cancels without a new
    // operation generation, so no approval continuation and no effect can
    // dispatch from an elapsed deadline.
    internal static void ExpirePendingApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPendingApprovalState pending,
        Timestamp now)
    {
        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, pending.StepId, StringComparison.Ordinal) &&
            candidate.Kind == NyxIdChatStepKind.Tool &&
            candidate.Status == NyxIdChatStepStatus.Waiting &&
            string.Equals(
                candidate.ApprovalRequestId,
                pending.ApprovalRequestId,
                StringComparison.Ordinal));
        if (step is not null)
        {
            step.Status = NyxIdChatStepStatus.Cancelled;
            step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
            step.FailureCode = ApprovalExpired;
            step.SafeMessage = ApprovalExpiredMessage;
            if (step.Operation is not null)
            {
                step.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
                step.Operation.TerminalCode = ApprovalExpired;
                step.Operation.SafeMessage = ApprovalExpiredMessage;
                step.Operation.CompletedAt = now.Clone();
            }

            step.UpdatedAt = now.Clone();
            step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        }

        state.PendingApproval = null;
        if (state.ActiveTask is null || state.ActiveTurn is null)
        {
            state.UpdatedAt = now.Clone();
            return;
        }

        NyxIdChatTaskTransitionPolicy.RefreshTaskOutcome(state);
        state.ActiveTask.UpdatedAt = now.Clone();
        FinalizeDerivedState(state, now);
    }

    private static void ActivateStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        Timestamp now)
    {
        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Operation.RequestedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.FailureCode = string.Empty;
        state.ActiveTask.SafeMessage = string.Empty;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation.Key.OperationId;
        state.ActiveTask.UpdatedAt = now.Clone();
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.FailureCode = string.Empty;
        state.ActiveTurn.SafeMessage = string.Empty;
        state.ActiveTurn.TerminalAt = null;
    }

    private static void StampReconciledState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        var step = FindCurrentStep(state, key);
        if (step?.Operation is not null)
        {
            step.Operation.CompletedAt = now.Clone();
            step.Operation.PendingStepChangedProgressSequence = 0;
            step.Operation.StepChangedDueAt = null;
            step.UpdatedAt = now.Clone();
            step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        }
        state.ActiveTask.UpdatedAt = now.Clone();
    }

    private static void FinalizeDerivedState(
        NyxIdChatConversationGAgentState state,
        Timestamp now)
    {
        if (state.ActiveTurn.Status != NyxIdChatTurnStatus.Active)
        {
            state.ActiveTurn.TerminalAt = now.Clone();
            AddTerminalSummary(state, state.ActiveTurn);
        }

        state.LatestTurn = state.ActiveTurn.Clone();
        state.UpdatedAt = now.Clone();
    }

    private static void AddTerminalSummary(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        const int historyLimit = 32;
        var existing = state.RecentTerminalTurns.FirstOrDefault(summary =>
            string.Equals(summary.TurnId, turn.TurnId, StringComparison.Ordinal));
        if (existing is null)
        {
            state.RecentTerminalTurns.Add(new NyxIdChatTurnSummary
            {
                TurnId = turn.TurnId,
                TaskId = turn.TaskId,
                Status = turn.Status,
                FailureCode = turn.FailureCode,
                SafeMessage = turn.SafeMessage,
                TerminalAt = turn.TerminalAt?.Clone(),
            });
        }
        else
        {
            existing.TaskId = turn.TaskId;
            existing.Status = turn.Status;
            existing.FailureCode = turn.FailureCode;
            existing.SafeMessage = turn.SafeMessage;
            existing.TerminalAt = turn.TerminalAt?.Clone();
        }

        while (state.RecentTerminalTurns.Count > historyLimit)
            state.RecentTerminalTurns.RemoveAt(0);
    }

    private static NyxIdChatTaskStepState? FindCurrentStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey? key)
    {
        if (key is null || state.ActiveTask is null)
            return null;
        return state.ActiveTask.Steps.FirstOrDefault(step =>
            KeysEqual(step.Operation?.Key, key));
    }

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static NyxIdChatTaskLifecycleDecision FromTransition(
        NyxIdChatTransitionDecision decision,
        NyxIdChatOperationDispatchCommand? nextCommand) =>
        new(
            decision.Outcome,
            decision.ReasonCode,
            decision.SafeMessage,
            decision.State,
            nextCommand);
}
