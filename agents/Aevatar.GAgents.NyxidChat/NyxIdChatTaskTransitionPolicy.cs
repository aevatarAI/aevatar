using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatTransitionDecision(
    NyxIdChatTransitionOutcome Outcome,
    string ReasonCode,
    string SafeMessage,
    NyxIdChatConversationGAgentState State);

/// <summary>
/// Pure task transition policy for the NyxIdChat conversation authority.
/// The policy validates the complete reconciliation key and always returns a
/// cloned state so callers cannot accidentally mutate committed actor state.
/// </summary>
public static class NyxIdChatTaskTransitionPolicy
{
    public const string OperationStarted = "NYXID_CHAT_OPERATION_STARTED";
    public const string OperationAlreadyStarted = "NYXID_CHAT_OPERATION_ALREADY_STARTED";
    public const string OperationAlreadyRunning = "NYXID_CHAT_OPERATION_ALREADY_RUNNING";
    public const string OperationSucceeded = "NYXID_CHAT_OPERATION_SUCCEEDED";
    public const string OperationFailed = "NYXID_CHAT_OPERATION_FAILED";
    public const string OperationAlreadyReconciled = "NYXID_CHAT_OPERATION_ALREADY_RECONCILED";
    public const string IdentityMismatch = "NYXID_CHAT_IDENTITY_MISMATCH";
    public const string OperationKeyMismatch = "NYXID_CHAT_OPERATION_KEY_MISMATCH";
    public const string InvalidOperationGeneration = "NYXID_CHAT_INVALID_OPERATION_GENERATION";
    public const string StepAlreadyTerminal = "NYXID_CHAT_STEP_ALREADY_TERMINAL";
    public const string ControlFenceActive = "NYXID_CHAT_CONTROL_FENCE_ACTIVE";
    public const string TerminalOutcomeConflict = "NYXID_CHAT_TERMINAL_OUTCOME_CONFLICT";
    public const string PostconditionNotVerified = "NYXID_CHAT_POSTCONDITION_NOT_VERIFIED";
    public const string StateNotActive = "NYXID_CHAT_STATE_NOT_ACTIVE";
    public const string StepNotFound = "NYXID_CHAT_STEP_NOT_FOUND";
    public const string StepKindMismatch = "NYXID_CHAT_STEP_KIND_MISMATCH";
    public const string ResultMissing = "NYXID_CHAT_OPERATION_RESULT_MISSING";
    public const string OperationInterrupted = "NYXID_CHAT_OPERATION_INTERRUPTED";
    public const string OperationOutcomeUncertain =
        "NYXID_CHAT_OPERATION_OUTCOME_UNCERTAIN";

    private const string OperationInterruptedMessage =
        "The operation was interrupted and was not replayed automatically.";
    private const string OperationOutcomeUncertainMessage =
        "The external operation may have changed state before recovery.";

    public static NyxIdChatTransitionDecision StartOperation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        NyxIdChatStepKind kind,
        bool mayChangeExternalState)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);

        if (!TryResolveStep(state, key, out var currentStep, out var reasonCode))
            return Reject(state, reasonCode);

        if (HasActiveControlFence(state))
            return Reject(state, ControlFenceActive);

        if (state.ActiveTask.Status != NyxIdChatTaskStatus.Active ||
            state.ActiveTurn.Status != NyxIdChatTurnStatus.Active)
        {
            return Reject(state, StateNotActive);
        }

        if (kind == NyxIdChatStepKind.Unspecified || currentStep.Kind != kind)
            return Reject(state, StepKindMismatch);

        if (currentStep.Operation is not null)
        {
            if (KeysEqual(currentStep.Operation.Key, key) &&
                IsInFlight(currentStep.Operation.Phase))
            {
                return Idempotent(state, OperationAlreadyStarted);
            }

            if (IsInFlight(currentStep.Operation.Phase))
                return Reject(state, OperationAlreadyRunning);
        }

        if (IsNonRetryableTerminal(currentStep.Status))
            return Reject(state, StepAlreadyTerminal);

        if (currentStep.Status == NyxIdChatStepStatus.Failed &&
            !ResolveAvailableActions(currentStep).Retry)
        {
            return Reject(state, StepAlreadyTerminal);
        }

        var expectedGeneration = currentStep.Operation is null
            ? 1
            : currentStep.Operation.Key.OperationGeneration + 1;
        if (key.OperationGeneration != expectedGeneration)
            return Reject(state, InvalidOperationGeneration);

        var next = state.Clone();
        var step = FindStep(next, key.StepId)!;
        step.Status = NyxIdChatStepStatus.Running;
        if (currentStep.Operation is null)
            step.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        step.FailureCode = string.Empty;
        step.SafeMessage = string.Empty;
        step.Operation = new NyxIdChatOperationState
        {
            Key = key.Clone(),
            Kind = kind,
            Phase = NyxIdChatOperationPhase.Requested,
            MayChangeExternalState = mayChangeExternalState,
        };
        step.AvailableActions = ResolveAvailableActions(step);
        next.ActiveTask.ActiveStepId = key.StepId;
        next.ActiveTask.ActiveOperationId = key.OperationId;

        return Accept(next, OperationStarted);
    }

    public static NyxIdChatTransitionDecision ReconcileOperation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);

        if (!TryResolveStep(state, signal.Key, out var currentStep, out _))
            return Reject(state, OperationKeyMismatch);

        if (currentStep.Operation?.Key is null ||
            !KeysEqual(currentStep.Operation.Key, signal.Key))
        {
            return Reject(state, OperationKeyMismatch);
        }

        if (!TryClassifyResult(signal, out var classified))
            return Reject(state, ResultMissing);

        if (classified.RequiresVerifiedPostcondition && !classified.PostconditionVerified)
            return Reject(state, PostconditionNotVerified);

        if (IsTerminal(currentStep.Status) || IsTerminal(currentStep.Operation.Phase))
        {
            return TerminalReplayMatches(currentStep, classified)
                ? Idempotent(state, OperationAlreadyReconciled)
                : Reject(state, TerminalOutcomeConflict);
        }

        var next = state.Clone();
        var step = FindStep(next, signal.Key.StepId)!;
        ApplyResult(step, classified);
        step.AvailableActions = ResolveAvailableActions(step);
        next.ActiveTask.ActiveStepId = string.Empty;
        next.ActiveTask.ActiveOperationId = string.Empty;
        ApplyTaskOutcome(next);

        return Accept(
            next,
            classified.StepStatus == NyxIdChatStepStatus.Done
                ? OperationSucceeded
                : OperationFailed);
    }

    public static NyxIdChatAvailableActions ResolveAvailableActions(
        NyxIdChatTaskStepState step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var retryableStatus = step.Status is
            NyxIdChatStepStatus.Failed or
            NyxIdChatStepStatus.Cancelled or
            NyxIdChatStepStatus.Uncertain;
        var effectAllowsRetry = step.ExternalEffect is
            NyxIdChatEffectEvidence.NotStarted or
            NyxIdChatEffectEvidence.NotApplied;
        var recoverableStatus = step.Status is
            NyxIdChatStepStatus.Waiting or
            NyxIdChatStepStatus.Failed or
            NyxIdChatStepStatus.Cancelled or
            NyxIdChatStepStatus.Uncertain;

        return new NyxIdChatAvailableActions
        {
            Retry = step.Kind is (NyxIdChatStepKind.Llm or NyxIdChatStepKind.Tool) &&
                    step.RetryInputRebuildable &&
                    (step.Kind != NyxIdChatStepKind.Tool ||
                     step.RetryToolInput?.OperationAdmission is not null) &&
                    retryableStatus &&
                    effectAllowsRetry,
            Skip = recoverableStatus && (!step.Required || step.SafeToSkip),
            Stop = step.Status is
                NyxIdChatStepStatus.Planned or
                NyxIdChatStepStatus.Waiting or
                NyxIdChatStepStatus.Running,
        };
    }

    public static NyxIdChatOperationResultSignal? BuildInterruptedRecoveryResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);

        if (!TryResolveStep(state, key, out var step, out _) ||
            step.Operation?.Key is null ||
            !KeysEqual(step.Operation.Key, key) ||
            !IsInFlight(step.Operation.Phase) ||
            step.Kind is not (NyxIdChatStepKind.Llm or NyxIdChatStepKind.Tool))
        {
            return null;
        }

        var mayHaveChanged = step.MayChangeExternalState ||
                             step.Operation.MayChangeExternalState;
        return new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = mayHaveChanged
                    ? OperationOutcomeUncertain
                    : OperationInterrupted,
                SafeMessage = mayHaveChanged
                    ? OperationOutcomeUncertainMessage
                    : OperationInterruptedMessage,
                ExternalEffect = mayHaveChanged
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied,
            },
        };
    }

    private static bool TryResolveStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey? key,
        out NyxIdChatTaskStepState step,
        out string reasonCode)
    {
        step = null!;
        reasonCode = IdentityMismatch;
        if (key is null ||
            state.ActiveTurn is null ||
            state.ActiveTask is null ||
            string.IsNullOrWhiteSpace(key.ConversationActorId) ||
            string.IsNullOrWhiteSpace(key.TurnId) ||
            string.IsNullOrWhiteSpace(key.TaskId) ||
            string.IsNullOrWhiteSpace(key.StepId) ||
            string.IsNullOrWhiteSpace(key.OperationId) ||
            !string.Equals(state.ConversationActorId, key.ConversationActorId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTurn.TaskId, key.TaskId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTask.TaskId, key.TaskId, StringComparison.Ordinal))
        {
            return false;
        }

        var resolved = FindStep(state, key.StepId);
        if (resolved is null)
        {
            reasonCode = IdentityMismatch;
            return false;
        }

        var activeTurnMatches =
            string.Equals(state.ActiveTurn.TurnId, key.TurnId, StringComparison.Ordinal) &&
            string.Equals(state.ActiveTask.TurnId, key.TurnId, StringComparison.Ordinal);
        if (!activeTurnMatches &&
            !NyxIdChatActionContinuationCorrelation.TryMatch(
                state,
                state.ActiveTask,
                state.ActiveTurn,
                key,
                out _))
            return false;

        step = resolved;
        return true;
    }

    private static NyxIdChatTaskStepState? FindStep(
        NyxIdChatConversationGAgentState state,
        string stepId) =>
        state.ActiveTask?.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, stepId, StringComparison.Ordinal));

    private static bool HasActiveControlFence(NyxIdChatConversationGAgentState state) =>
        state.ControlFence is
        {
            Kind: not NyxIdChatControlKind.Unspecified,
            Outcome: NyxIdChatControlOutcome.Accepted or NyxIdChatControlOutcome.Uncancellable,
        };

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static bool IsInFlight(NyxIdChatOperationPhase phase) =>
        phase is
            NyxIdChatOperationPhase.Requested or
            NyxIdChatOperationPhase.Dispatched or
            NyxIdChatOperationPhase.Running;

    private static bool IsTerminal(NyxIdChatOperationPhase phase) =>
        phase is
            NyxIdChatOperationPhase.Succeeded or
            NyxIdChatOperationPhase.Failed or
            NyxIdChatOperationPhase.Cancelled or
            NyxIdChatOperationPhase.Uncertain;

    private static bool IsTerminal(NyxIdChatStepStatus status) =>
        status is
            NyxIdChatStepStatus.Done or
            NyxIdChatStepStatus.Failed or
            NyxIdChatStepStatus.Skipped or
            NyxIdChatStepStatus.Cancelled or
            NyxIdChatStepStatus.Uncertain;

    private static bool IsNonRetryableTerminal(NyxIdChatStepStatus status) =>
        status is
            NyxIdChatStepStatus.Done or
            NyxIdChatStepStatus.Skipped or
            NyxIdChatStepStatus.Cancelled or
            NyxIdChatStepStatus.Uncertain;

    private static bool TryClassifyResult(
        NyxIdChatOperationResultSignal signal,
        out ClassifiedOperationResult result)
    {
        switch (signal.ResultCase)
        {
            case NyxIdChatOperationResultSignal.ResultOneofCase.Llm:
                result = ClassifiedOperationResult.Success(
                    NyxIdChatEffectEvidence.NotApplied);
                return true;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Tool:
                result = ClassifyToolResult(signal.Tool);
                return true;
            case NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition:
                result = ClassifyPostconditionResult(signal.ActionPostcondition);
                return true;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Failure:
                result = ClassifyFailure(signal.Failure);
                return true;
            case NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification:
                result = ClassifyToolVerification(signal.ToolVerification);
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static ClassifiedOperationResult ClassifyToolResult(
        NyxIdChatToolOperationResult tool)
    {
        var receipt = tool.Receipt;
        return receipt?.Status switch
        {
            AgentToolReceiptStatus.Success
                when tool.ExternalEffect == NyxIdChatEffectEvidence.MayHaveChanged =>
                new ClassifiedOperationResult(
                    NyxIdChatStepStatus.Waiting,
                    NyxIdChatOperationPhase.Succeeded,
                    NyxIdChatEffectEvidence.MayHaveChanged,
                    string.Empty,
                    string.Empty,
                    false,
                    false),
            AgentToolReceiptStatus.Success => ClassifiedOperationResult.Success(
                NormalizeEffect(tool.ExternalEffect, NyxIdChatEffectEvidence.NotApplied)),
            AgentToolReceiptStatus.ApprovalRequired or
                AgentToolReceiptStatus.AuthorizationRequired => new ClassifiedOperationResult(
                    NyxIdChatStepStatus.Waiting,
                    NyxIdChatOperationPhase.Succeeded,
                    NormalizeEffect(tool.ExternalEffect, NyxIdChatEffectEvidence.NotStarted),
                    receipt.ErrorCode,
                    receipt.ErrorMessage,
                    false,
                    false),
            AgentToolReceiptStatus.Denied => new ClassifiedOperationResult(
                NyxIdChatStepStatus.Cancelled,
                NyxIdChatOperationPhase.Cancelled,
                NormalizeEffect(tool.ExternalEffect, NyxIdChatEffectEvidence.NotApplied),
                receipt.ErrorCode,
                receipt.ErrorMessage,
                false,
                false),
            _ => ClassifiedOperationResult.Failure(
                NormalizeEffect(tool.ExternalEffect, NyxIdChatEffectEvidence.NotApplied),
                receipt?.ErrorCode,
                receipt?.ErrorMessage),
        };
    }

    private static ClassifiedOperationResult ClassifyPostconditionResult(
        NyxIdChatActionPostconditionResult postcondition) =>
        postcondition.Disposition switch
        {
            NyxIdChatActionDisposition.Completed when postcondition.Verified =>
                ClassifiedOperationResult.Success(NyxIdChatEffectEvidence.Confirmed) with
                {
                    RequiresVerifiedPostcondition = true,
                    PostconditionVerified = true,
                },
            NyxIdChatActionDisposition.Completed => new ClassifiedOperationResult(
                NyxIdChatStepStatus.Waiting,
                NyxIdChatOperationPhase.Running,
                NyxIdChatEffectEvidence.NotStarted,
                postcondition.FailureCode,
                postcondition.SafeMessage,
                true,
                false),
            NyxIdChatActionDisposition.Declined or
                NyxIdChatActionDisposition.Cancelled or
                NyxIdChatActionDisposition.Expired => new ClassifiedOperationResult(
                    NyxIdChatStepStatus.Cancelled,
                    NyxIdChatOperationPhase.Cancelled,
                    NyxIdChatEffectEvidence.NotApplied,
                    postcondition.FailureCode,
                    postcondition.SafeMessage,
                    false,
                    false),
            _ => ClassifiedOperationResult.Failure(
                NyxIdChatEffectEvidence.NotApplied,
                postcondition.FailureCode,
                postcondition.SafeMessage),
        };

    private static ClassifiedOperationResult ClassifyFailure(
        NyxIdChatOperationFailure failure)
    {
        var effect = NormalizeEffect(
            failure.ExternalEffect,
            NyxIdChatEffectEvidence.NotApplied);
        return effect == NyxIdChatEffectEvidence.MayHaveChanged
            ? new ClassifiedOperationResult(
                NyxIdChatStepStatus.Uncertain,
                NyxIdChatOperationPhase.Uncertain,
                effect,
                failure.FailureCode,
                failure.SafeMessage,
                false,
                false)
            : ClassifiedOperationResult.Failure(
                effect,
                failure.FailureCode,
                failure.SafeMessage);
    }

    private static ClassifiedOperationResult ClassifyToolVerification(
        NyxIdChatToolVerificationResult verification) => verification.Disposition switch
    {
        NyxIdChatToolVerificationDisposition.Applied =>
            ClassifiedOperationResult.Success(NyxIdChatEffectEvidence.Confirmed) with
            {
                RequiresVerifiedPostcondition = true,
                PostconditionVerified = true,
            },
        NyxIdChatToolVerificationDisposition.NotApplied =>
            ClassifiedOperationResult.Success(NyxIdChatEffectEvidence.NotApplied),
        _ => new ClassifiedOperationResult(
            NyxIdChatStepStatus.Uncertain,
            NyxIdChatOperationPhase.Uncertain,
            NyxIdChatEffectEvidence.MayHaveChanged,
            verification.FailureCode,
            verification.SafeMessage,
            false,
            false),
    };

    internal static void RefreshTaskOutcome(NyxIdChatConversationGAgentState state) =>
        ApplyTaskOutcome(state);

    private static NyxIdChatEffectEvidence NormalizeEffect(
        NyxIdChatEffectEvidence effect,
        NyxIdChatEffectEvidence fallback) =>
        effect == NyxIdChatEffectEvidence.Unspecified ? fallback : effect;

    private static void ApplyResult(
        NyxIdChatTaskStepState step,
        ClassifiedOperationResult result)
    {
        step.Status = result.StepStatus;
        step.ExternalEffect = result.ExternalEffect;
        step.FailureCode = result.FailureCode;
        step.SafeMessage = result.SafeMessage;
        step.Operation.Phase = result.OperationPhase;
        step.Operation.TerminalCode = result.FailureCode;
        step.Operation.SafeMessage = result.SafeMessage;
        foreach (var substep in step.Substeps.Where(static candidate =>
                     candidate.Status == NyxIdChatSubstepStatus.Running))
        {
            substep.Status = result.StepStatus == NyxIdChatStepStatus.Done
                ? NyxIdChatSubstepStatus.Done
                : NyxIdChatSubstepStatus.Failed;
        }
    }

    private static void ApplyTaskOutcome(NyxIdChatConversationGAgentState state)
    {
        var requiredFailure = state.ActiveTask.Steps.FirstOrDefault(step =>
            step.CancelledInPlanRevision == 0 &&
            step.Required && step.Status is
                NyxIdChatStepStatus.Failed or
                NyxIdChatStepStatus.Cancelled or
                NyxIdChatStepStatus.Uncertain &&
            !HasRecoveryAction(step));
        if (requiredFailure is not null)
        {
            SetTerminalOutcome(
                state,
                NyxIdChatTaskStatus.Failed,
                NyxIdChatTurnStatus.Failed,
                requiredFailure.FailureCode,
                requiredFailure.SafeMessage);
            return;
        }

        var recoverableStep = state.ActiveTask.Steps.FirstOrDefault(step =>
            step.CancelledInPlanRevision == 0 &&
            step.Status is
                NyxIdChatStepStatus.Failed or
                NyxIdChatStepStatus.Cancelled or
                NyxIdChatStepStatus.Uncertain &&
            HasRecoveryAction(step));
        if (recoverableStep is not null)
        {
            SetRecoverableOutcome(state, recoverableStep);
            return;
        }

        var hasActiveStep = state.ActiveTask.Steps.Any(step =>
            step.Status is
                NyxIdChatStepStatus.Planned or
                NyxIdChatStepStatus.Waiting or
                NyxIdChatStepStatus.Running);
        if (hasActiveStep)
        {
            state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
            state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
            MirrorLatestTurn(state);
            return;
        }

        SetTerminalOutcome(
            state,
            NyxIdChatTaskStatus.Succeeded,
            NyxIdChatTurnStatus.Succeeded,
            string.Empty,
            string.Empty);
    }

    private static bool HasRecoveryAction(NyxIdChatTaskStepState step)
    {
        var actions = ResolveAvailableActions(step);
        return actions.Retry || actions.Skip;
    }

    private static void SetRecoverableOutcome(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step)
    {
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = string.Empty;
        state.ActiveTask.FailureCode = step.FailureCode;
        state.ActiveTask.SafeMessage = step.SafeMessage;
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.FailureCode = step.FailureCode;
        state.ActiveTurn.SafeMessage = step.SafeMessage;
        MirrorLatestTurn(state);
    }

    private static void SetTerminalOutcome(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStatus taskStatus,
        NyxIdChatTurnStatus turnStatus,
        string failureCode,
        string safeMessage)
    {
        state.ActiveTask.Status = taskStatus;
        state.ActiveTask.FailureCode = failureCode;
        state.ActiveTask.SafeMessage = safeMessage;
        state.ActiveTurn.Status = turnStatus;
        state.ActiveTurn.FailureCode = failureCode;
        state.ActiveTurn.SafeMessage = safeMessage;
        MirrorLatestTurn(state);
    }

    private static void MirrorLatestTurn(NyxIdChatConversationGAgentState state)
    {
        if (state.ActiveTurn is not null)
            state.LatestTurn = state.ActiveTurn.Clone();
    }

    private static bool TerminalReplayMatches(
        NyxIdChatTaskStepState step,
        ClassifiedOperationResult result) =>
        step.Status == result.StepStatus &&
        step.Operation.Phase == result.OperationPhase &&
        step.ExternalEffect == result.ExternalEffect &&
        string.Equals(step.FailureCode, result.FailureCode, StringComparison.Ordinal) &&
        string.Equals(step.SafeMessage, result.SafeMessage, StringComparison.Ordinal);

    private static NyxIdChatTransitionDecision Accept(
        NyxIdChatConversationGAgentState state,
        string reasonCode) =>
        new(
            NyxIdChatTransitionOutcome.Accepted,
            reasonCode,
            string.Empty,
            state);

    private static NyxIdChatTransitionDecision Reject(
        NyxIdChatConversationGAgentState state,
        string reasonCode) =>
        new(
            NyxIdChatTransitionOutcome.Rejected,
            reasonCode,
            string.Empty,
            state.Clone());

    private static NyxIdChatTransitionDecision Idempotent(
        NyxIdChatConversationGAgentState state,
        string reasonCode) =>
        new(
            NyxIdChatTransitionOutcome.Idempotent,
            reasonCode,
            string.Empty,
            state.Clone());

    private readonly record struct ClassifiedOperationResult(
        NyxIdChatStepStatus StepStatus,
        NyxIdChatOperationPhase OperationPhase,
        NyxIdChatEffectEvidence ExternalEffect,
        string FailureCode,
        string SafeMessage,
        bool RequiresVerifiedPostcondition,
        bool PostconditionVerified)
    {
        public static ClassifiedOperationResult Success(NyxIdChatEffectEvidence effect) =>
            new(
                NyxIdChatStepStatus.Done,
                NyxIdChatOperationPhase.Succeeded,
                effect,
                string.Empty,
                string.Empty,
                false,
                false);

        public static ClassifiedOperationResult Failure(
            NyxIdChatEffectEvidence effect,
            string? failureCode,
            string? safeMessage) =>
            new(
                NyxIdChatStepStatus.Failed,
                NyxIdChatOperationPhase.Failed,
                effect,
                failureCode ?? string.Empty,
                safeMessage ?? string.Empty,
                false,
                false);
    }
}
