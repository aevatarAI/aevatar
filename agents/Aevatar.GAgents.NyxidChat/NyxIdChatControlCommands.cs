using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatControlDecision(
    bool ShouldCommit,
    NyxIdChatConversationGAgentState FencedState,
    NyxIdChatConversationGAgentState State,
    NyxIdChatControlFenceState Result,
    NyxIdChatContinuationAdmissionState? Admission,
    bool StartContinuationNow);

public sealed record NyxIdChatLateOperationEvidenceDecision(
    bool IsFencedOperation,
    bool ShouldCommit,
    NyxIdChatConversationGAgentState State,
    NyxIdChatOperationPhase OperationPhase,
    NyxIdChatEffectEvidence ExternalEffect,
    string TerminalCode,
    string SafeMessage);

public sealed record NyxIdChatStepControlDecision(
    bool ShouldCommit,
    bool ShouldDispatch,
    NyxIdChatConversationGAgentState State,
    NyxIdChatStepControlResultState Result,
    NyxIdChatOperationDispatchCommand? NextCommand);

/// <summary>
/// Pure actor-owned policy for stop and steering commands. It never performs
/// cancellation, dispatch, projection, or I/O; the conversation actor commits
/// the returned fence before acting on any continuation decision.
/// </summary>
public static class NyxIdChatControlCommands
{
    public const string StopAccepted = "NYXID_CHAT_STOP_ACCEPTED";
    public const string StopUncancellable = "NYXID_CHAT_STOP_UNCANCELLABLE";
    public const string SteeringAccepted = "NYXID_CHAT_STEERING_ACCEPTED";
    public const string SteeringAcceptedForLater =
        "NYXID_CHAT_STEERING_ACCEPTED_FOR_LATER";
    public const string AlreadyTerminal = "NYXID_CHAT_ALREADY_TERMINAL";
    public const string IdentityMismatch = "NYXID_CHAT_CONTROL_IDENTITY_MISMATCH";
    public const string StateVersionMismatch = "NYXID_CHAT_STATE_VERSION_MISMATCH";
    public const string ControlConflict = "NYXID_CHAT_CONTROL_CONFLICT";
    public const string StepControlConflict = "NYXID_CHAT_STEP_CONTROL_CONFLICT";
    public const string StepActionUnavailable = "NYXID_CHAT_STEP_ACTION_UNAVAILABLE";
    public const string StepRetryAccepted = "NYXID_CHAT_STEP_RETRY_ACCEPTED";
    public const string StepSkipAccepted = "NYXID_CHAT_STEP_SKIP_ACCEPTED";
    public const string StepIdentityMismatch = "NYXID_CHAT_STEP_IDENTITY_MISMATCH";
    public const string OperationGenerationMismatch =
        "NYXID_CHAT_OPERATION_GENERATION_MISMATCH";

    public const string ActiveTurnRequiresSteering =
        "ACTIVE_TURN_REQUIRES_STEERING";
    public const string ActiveTurnRequiresSteeringMessage =
        "This conversation already has active work. Submit a steering command for the active turn.";

    private const string StopAcceptedMessage = "The active chat turn was stopped.";
    private const string StopUncancellableMessage =
        "The chat turn was fenced, but the in-flight operation could not be proven cancelled.";
    private const string SteeringAcceptedMessage =
        "The steering instruction was accepted at a safe checkpoint.";
    private const string SteeringAcceptedForLaterMessage =
        "The steering instruction was accepted and will continue after the in-flight operation reaches a safe checkpoint.";
    private const string AlreadyTerminalMessage = "The requested chat turn is already terminal.";
    private const string IdentityMismatchMessage =
        "The control command did not match the active conversation turn.";
    private const string StateVersionMismatchMessage =
        "The control command was based on a stale conversation state version.";
    private const string ControlConflictMessage =
        "A different control command already fenced this chat turn.";
    private const string StepControlConflictMessage =
        "The step control request identity was already used with different content.";
    private const string StepActionUnavailableMessage =
        "The requested step action is not available at the current committed state.";
    private const string StepRetryAcceptedMessage =
        "The step retry was accepted for dispatch.";
    private const string StepSkipAcceptedMessage = "The step was skipped.";
    private const string StepIdentityMismatchMessage =
        "The step control command did not match this conversation task step.";
    private const string OperationGenerationMismatchMessage =
        "The step control command targeted a stale operation generation.";

    public static NyxIdChatStepControlDecision Retry(
        NyxIdChatConversationGAgentState state,
        NyxIdChatRetryStepCommand command,
        long stateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var input = StepControlInput.From(command);
        if (FindExactStepControlReplay(state, input) is { } replay)
        {
            var nextCommand = CanRedispatchRetry(state, replay)
                ? BuildRetryDispatch(state, command, replay.OperationGeneration)
                : null;
            return new NyxIdChatStepControlDecision(
                ShouldCommit: false,
                ShouldDispatch: nextCommand is not null,
                state.Clone(),
                replay.Clone(),
                nextCommand);
        }

        if (HasStepControlRequestIdentity(state, input))
            return RejectStepControl(state, input, StepControlConflict, StepControlConflictMessage, now);

        if (!TryResolveStepControlTarget(state, input, out var step))
            return RejectStepControl(state, input, StepIdentityMismatch, StepIdentityMismatchMessage, now);

        if (!MatchesExpectedVersion(input.ExpectedStateVersion, stateVersion))
            return RejectStepControl(state, input, StateVersionMismatch, StateVersionMismatchMessage, now);

        if (step.Operation?.Key is null ||
            step.Operation.Key.OperationGeneration != input.ExpectedOperationGeneration)
        {
            return RejectStepControl(
                state,
                input,
                OperationGenerationMismatch,
                OperationGenerationMismatchMessage,
                now);
        }

        if (IsTerminal(state.ActiveTurn.Status) ||
            state.ActiveTask.Status != NyxIdChatTaskStatus.Active)
        {
            return RejectStepControl(
                state,
                input,
                StepActionUnavailable,
                StepActionUnavailableMessage,
                now);
        }

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        if (!actions.Retry || !step.RetryInputRebuildable || HasAcceptedFence(state))
            return RejectStepControl(state, input, StepActionUnavailable, StepActionUnavailableMessage, now);

        var generation = step.Operation.Key.OperationGeneration + 1;
        var next = state.Clone();
        var retried = next.ActiveTask.Steps.First(candidate =>
            string.Equals(candidate.StepId, input.StepId, StringComparison.Ordinal));
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = next.ConversationActorId,
            TurnId = next.ActiveTurn.TurnId,
            TaskId = next.ActiveTask.TaskId,
            StepId = retried.StepId,
            OperationId = BuildStableIdentity(
                "operation",
                next.ConversationActorId,
                next.ActiveTurn.TurnId,
                next.ActiveTask.TaskId,
                retried.StepId,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            OperationGeneration = generation,
        };
        retried.Status = NyxIdChatStepStatus.Running;
        retried.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        retried.FailureCode = string.Empty;
        retried.SafeMessage = string.Empty;
        retried.Operation = new NyxIdChatOperationState
        {
            Key = key.Clone(),
            Kind = NyxIdChatStepKind.Llm,
            Phase = NyxIdChatOperationPhase.Requested,
            RequestedAt = now.Clone(),
        };
        retried.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(retried);
        retried.UpdatedAt = now.Clone();
        next.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        next.ActiveTask.ActiveStepId = retried.StepId;
        next.ActiveTask.ActiveOperationId = key.OperationId;
        next.ActiveTask.FailureCode = string.Empty;
        next.ActiveTask.SafeMessage = string.Empty;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        next.ActiveTurn.FailureCode = string.Empty;
        next.ActiveTurn.SafeMessage = string.Empty;
        next.ActiveTurn.TerminalAt = null;
        next.LatestTurn = next.ActiveTurn.Clone();
        var result = BuildStepControlResult(
            input,
            generation,
            NyxIdChatTransitionOutcome.Accepted,
            StepRetryAccepted,
            StepRetryAcceptedMessage,
            now);
        RecordStepControlResult(next, result, now);
        return new NyxIdChatStepControlDecision(
            ShouldCommit: true,
            ShouldDispatch: true,
            next,
            result,
            BuildRetryDispatch(next, command, generation));
    }

    public static NyxIdChatStepControlDecision Skip(
        NyxIdChatConversationGAgentState state,
        NyxIdChatSkipStepCommand command,
        long stateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var input = StepControlInput.From(command);
        if (FindExactStepControlReplay(state, input) is { } replay)
        {
            return new NyxIdChatStepControlDecision(
                ShouldCommit: false,
                ShouldDispatch: false,
                state.Clone(),
                replay.Clone(),
                NextCommand: null);
        }

        if (HasStepControlRequestIdentity(state, input))
            return RejectStepControl(state, input, StepControlConflict, StepControlConflictMessage, now);

        if (!TryResolveStepControlTarget(state, input, out var step))
            return RejectStepControl(state, input, StepIdentityMismatch, StepIdentityMismatchMessage, now);

        if (!MatchesExpectedVersion(input.ExpectedStateVersion, stateVersion))
            return RejectStepControl(state, input, StateVersionMismatch, StateVersionMismatchMessage, now);

        if (step.Operation?.Key is null ||
            step.Operation.Key.OperationGeneration != input.ExpectedOperationGeneration)
        {
            return RejectStepControl(
                state,
                input,
                OperationGenerationMismatch,
                OperationGenerationMismatchMessage,
                now);
        }

        if (IsTerminal(state.ActiveTurn.Status) ||
            state.ActiveTask.Status != NyxIdChatTaskStatus.Active)
        {
            return RejectStepControl(
                state,
                input,
                StepActionUnavailable,
                StepActionUnavailableMessage,
                now);
        }

        if (!NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step).Skip || HasAcceptedFence(state))
            return RejectStepControl(state, input, StepActionUnavailable, StepActionUnavailableMessage, now);

        var next = state.Clone();
        var skipped = next.ActiveTask.Steps.First(candidate =>
            string.Equals(candidate.StepId, input.StepId, StringComparison.Ordinal));
        skipped.Status = NyxIdChatStepStatus.Skipped;
        skipped.FailureCode = string.Empty;
        skipped.SafeMessage = string.Empty;
        skipped.AvailableActions = new NyxIdChatAvailableActions();
        skipped.UpdatedAt = now.Clone();
        next.ActiveTask.ActiveStepId = string.Empty;
        next.ActiveTask.ActiveOperationId = string.Empty;
        ApplyTaskOutcomeAfterSkip(next, now);
        var result = BuildStepControlResult(
            input,
            input.ExpectedOperationGeneration,
            NyxIdChatTransitionOutcome.Accepted,
            StepSkipAccepted,
            StepSkipAcceptedMessage,
            now);
        RecordStepControlResult(next, result, now);
        return new NyxIdChatStepControlDecision(
            ShouldCommit: true,
            ShouldDispatch: false,
            next,
            result,
            NextCommand: null);
    }

    public static NyxIdChatLateOperationEvidenceDecision ReconcileLateOperationEvidence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        var unchanged = new NyxIdChatLateOperationEvidenceDecision(
            IsFencedOperation: HasAcceptedFenceFor(state, signal.Key),
            false,
            state.Clone(),
            NyxIdChatOperationPhase.Unspecified,
            NyxIdChatEffectEvidence.Unspecified,
            string.Empty,
            string.Empty);
        if (!unchanged.IsFencedOperation ||
            !TryResolveStep(state, signal.Key, out var currentStep) ||
            currentStep.Operation is null)
        {
            return unchanged;
        }

        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            (state.ControlFence?.Kind != NyxIdChatControlKind.Steering ||
             state.ContinuationAdmission?.Status !=
                 NyxIdChatContinuationAdmissionStatus.AcceptedForLater))
        {
            return unchanged;
        }

        var evidence = ClassifyLateOperationEvidence(currentStep, signal);
        if (evidence is null)
            return unchanged;

        if (IsTerminal(currentStep.Operation.Phase))
            return unchanged;

        if (currentStep.Operation.Phase is not (
                NyxIdChatOperationPhase.Dispatched or
                NyxIdChatOperationPhase.Running))
        {
            return unchanged;
        }

        var next = state.Clone();
        var step = next.ActiveTask.Steps.First(candidate =>
            KeysEqual(candidate.Operation?.Key, signal.Key));
        step.ExternalEffect = evidence.Value.Effect;
        step.Operation.Phase = evidence.Value.Phase;
        step.Operation.TerminalCode = evidence.Value.TerminalCode;
        step.Operation.SafeMessage = evidence.Value.SafeMessage;
        step.Operation.CompletedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = new NyxIdChatAvailableActions();
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();

        return new NyxIdChatLateOperationEvidenceDecision(
            IsFencedOperation: true,
            true,
            next,
            evidence.Value.Phase,
            evidence.Value.Effect,
            evidence.Value.TerminalCode,
            evidence.Value.SafeMessage);
    }

    public static NyxIdChatControlDecision Stop(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStopCommand command,
        long stateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var requestId = Normalize(command.StopRequestId);
        if (FindExactControlReplay(
                state,
                NyxIdChatControlKind.Stop,
                requestId,
                Normalize(command.ClientRequestId),
                Normalize(command.TurnId)) is { } replay)
        {
            return NoCommit(state, replay);
        }

        if (!MatchesControlIdentity(
                state,
                command.ScopeId,
                command.ConversationActorId,
                command.TurnId,
                requestId))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Stop,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                IdentityMismatch,
                IdentityMismatchMessage,
                now);
        }

        if (!MatchesExpectedVersion(command.ExpectedStateVersion, stateVersion))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Stop,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                StateVersionMismatch,
                StateVersionMismatchMessage,
                now);
        }

        if (HasAcceptedFence(state))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Stop,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                ControlConflict,
                ControlConflictMessage,
                now);
        }

        if (IsTerminal(state.ActiveTurn.Status))
        {
            return AlreadyTerminalDecision(
                state,
                NyxIdChatControlKind.Stop,
                requestId,
                command.ClientRequestId,
                now);
        }

        var physicallyInFlight = HasPhysicallyInFlightOperation(state);
        var outcome = physicallyInFlight
            ? NyxIdChatControlOutcome.Uncancellable
            : NyxIdChatControlOutcome.Accepted;
        var fence = BuildResult(
            state,
            NyxIdChatControlKind.Stop,
            requestId,
            command.ClientRequestId,
            outcome,
            physicallyInFlight ? StopUncancellable : StopAccepted,
            physicallyInFlight ? StopUncancellableMessage : StopAcceptedMessage,
            now);
        var next = ApplyTerminalFence(state, fence, now);
        return new NyxIdChatControlDecision(
            true,
            next,
            next,
            fence,
            Admission: null,
            StartContinuationNow: false);
    }

    public static NyxIdChatControlDecision Steer(
        NyxIdChatConversationGAgentState state,
        NyxIdChatSteeringCommand command,
        long stateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var requestId = Normalize(command.SteeringId);
        if (IsExactSteeringReplay(state, command, requestId))
        {
            return NoCommit(
                state,
                state.ControlFence ?? state.LatestControlResult,
                state.ContinuationAdmission,
                startContinuationNow:
                    state.ContinuationAdmission?.Status is
                        NyxIdChatContinuationAdmissionStatus.Accepted or
                        NyxIdChatContinuationAdmissionStatus.AcceptedForLater &&
                    !HasPhysicallyInFlightOperation(state));
        }

        if (!MatchesControlIdentity(
                state,
                command.ScopeId,
                command.ConversationActorId,
                command.TurnId,
                requestId) ||
            string.IsNullOrWhiteSpace(command.Instruction))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Steering,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                IdentityMismatch,
                IdentityMismatchMessage,
                now);
        }

        if (!MatchesExpectedVersion(command.ExpectedStateVersion, stateVersion))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Steering,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                StateVersionMismatch,
                StateVersionMismatchMessage,
                now);
        }

        if (HasAcceptedFence(state))
        {
            return Reject(
                state,
                NyxIdChatControlKind.Steering,
                requestId,
                command.ClientRequestId,
                command.TurnId,
                ControlConflict,
                ControlConflictMessage,
                now);
        }

        if (IsTerminal(state.ActiveTurn.Status))
        {
            return AlreadyTerminalDecision(
                state,
                NyxIdChatControlKind.Steering,
                requestId,
                command.ClientRequestId,
                now);
        }

        var physicallyInFlight = HasPhysicallyInFlightOperation(state);
        var fence = BuildResult(
            state,
            NyxIdChatControlKind.Steering,
            requestId,
            command.ClientRequestId,
            physicallyInFlight
                ? NyxIdChatControlOutcome.Uncancellable
                : NyxIdChatControlOutcome.Accepted,
            physicallyInFlight ? SteeringAcceptedForLater : SteeringAccepted,
            physicallyInFlight ? SteeringAcceptedForLaterMessage : SteeringAcceptedMessage,
            now);
        var fenced = ApplyTerminalFence(state, fence, now);
        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Steering,
            RequestId = requestId,
            ClientRequestId = Normalize(command.ClientRequestId),
            OriginTurnId = Normalize(command.TurnId),
            ContinuationTurnId = BuildStableIdentity(
                "turn",
                state.ConversationActorId,
                command.TurnId,
                requestId,
                "steering"),
            Status = physicallyInFlight
                ? NyxIdChatContinuationAdmissionStatus.AcceptedForLater
                : NyxIdChatContinuationAdmissionStatus.Accepted,
            ReasonCode = physicallyInFlight
                ? SteeringAcceptedForLater
                : SteeringAccepted,
            SafeMessage = physicallyInFlight
                ? SteeringAcceptedForLaterMessage
                : SteeringAcceptedMessage,
            CommittedAt = now.Clone(),
            Instruction = command.Instruction.Trim(),
        };
        admission.InputParts.AddRange(command.InputParts.Select(SanitizeInputPart));
        var final = fenced.Clone();
        final.ContinuationAdmission = admission.Clone();
        final.ProgressSequence = fenced.ProgressSequence + 1;
        final.UpdatedAt = now.Clone();
        return new NyxIdChatControlDecision(
            true,
            fenced,
            final,
            fence,
            admission,
            StartContinuationNow: !physicallyInFlight);
    }

    private static NyxIdChatControlDecision AlreadyTerminalDecision(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlKind kind,
        string requestId,
        string clientRequestId,
        Timestamp now)
    {
        var result = BuildResult(
            state,
            kind,
            requestId,
            clientRequestId,
            NyxIdChatControlOutcome.AlreadyTerminal,
            AlreadyTerminal,
            AlreadyTerminalMessage,
            now);
        var next = state.Clone();
        next.LatestControlResult = result.Clone();
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return new NyxIdChatControlDecision(
            true,
            next,
            next,
            result,
            Admission: null,
            StartContinuationNow: false);
    }

    private static NyxIdChatControlDecision Reject(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlKind kind,
        string requestId,
        string clientRequestId,
        string turnId,
        string reasonCode,
        string safeMessage,
        Timestamp now)
    {
        var result = new NyxIdChatControlFenceState
        {
            Kind = kind,
            RequestId = requestId,
            ClientRequestId = Normalize(clientRequestId),
            TurnId = Normalize(turnId),
            TaskId = state.ActiveTask?.TaskId ?? string.Empty,
            OperationGeneration = ResolveOperationGeneration(state),
            Outcome = NyxIdChatControlOutcome.Rejected,
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
            CommittedAt = now.Clone(),
        };
        var next = state.Clone();
        next.LatestControlResult = result.Clone();
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return new NyxIdChatControlDecision(
            true,
            next,
            next,
            result,
            Admission: null,
            StartContinuationNow: false);
    }

    private static NyxIdChatControlDecision NoCommit(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlFenceState? result,
        NyxIdChatContinuationAdmissionState? admission = null,
        bool startContinuationNow = false) =>
        new(
            false,
            state.Clone(),
            state.Clone(),
            result?.Clone() ?? new NyxIdChatControlFenceState(),
            admission?.Clone(),
            StartContinuationNow: startContinuationNow);

    private static NyxIdChatStepControlDecision RejectStepControl(
        NyxIdChatConversationGAgentState state,
        StepControlInput input,
        string reasonCode,
        string safeMessage,
        Timestamp now)
    {
        var result = BuildStepControlResult(
            input,
            input.ExpectedOperationGeneration,
            NyxIdChatTransitionOutcome.Rejected,
            reasonCode,
            safeMessage,
            now);
        var next = state.Clone();
        RecordStepControlResult(next, result, now);
        return new NyxIdChatStepControlDecision(
            ShouldCommit: true,
            ShouldDispatch: false,
            next,
            result,
            NextCommand: null);
    }

    private static NyxIdChatStepControlResultState BuildStepControlResult(
        StepControlInput input,
        long operationGeneration,
        NyxIdChatTransitionOutcome outcome,
        string reasonCode,
        string safeMessage,
        Timestamp now) =>
        new()
        {
            Kind = input.Kind,
            RequestId = input.RequestId,
            ClientRequestId = input.ClientRequestId,
            TurnId = input.TurnId,
            TaskId = input.TaskId,
            StepId = input.StepId,
            ExpectedOperationGeneration = input.ExpectedOperationGeneration,
            OperationGeneration = operationGeneration,
            Outcome = outcome,
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
            CommandId = input.CommandId,
            CorrelationId = input.CorrelationId,
            CommittedAt = now.Clone(),
            ExpectedStateVersion = input.ExpectedStateVersion,
            ScopeId = input.ScopeId,
            ConversationActorId = input.ConversationActorId,
        };

    private static void RecordStepControlResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStepControlResultState result,
        Timestamp now)
    {
        const int historyLimit = 32;
        state.LatestStepControlResult = result.Clone();
        if (!state.RecentStepControlResults.Any(existing =>
                StepControlResultsEquivalent(existing, result)))
        {
            state.RecentStepControlResults.Add(result.Clone());
        }
        while (state.RecentStepControlResults.Count > historyLimit)
            state.RecentStepControlResults.RemoveAt(0);
        state.ProgressSequence++;
        state.UpdatedAt = now.Clone();
    }

    private static NyxIdChatStepControlResultState? FindExactStepControlReplay(
        NyxIdChatConversationGAgentState state,
        StepControlInput input)
    {
        if (StepControlResultMatchesInput(state.LatestStepControlResult, input))
            return state.LatestStepControlResult;

        return state.RecentStepControlResults.LastOrDefault(result =>
            StepControlResultMatchesInput(result, input));
    }

    private static bool HasStepControlRequestIdentity(
        NyxIdChatConversationGAgentState state,
        StepControlInput input) =>
        StepControlRequestIdentityMatches(state.LatestStepControlResult, input) ||
        state.RecentStepControlResults.Any(result =>
            StepControlRequestIdentityMatches(result, input));

    private static bool StepControlRequestIdentityMatches(
        NyxIdChatStepControlResultState? result,
        StepControlInput input) =>
        result is not null &&
        result.Kind == input.Kind &&
        string.Equals(result.RequestId, input.RequestId, StringComparison.Ordinal);

    private static bool StepControlResultMatchesInput(
        NyxIdChatStepControlResultState? result,
        StepControlInput input) =>
        result is not null &&
        result.Kind == input.Kind &&
        string.Equals(result.RequestId, input.RequestId, StringComparison.Ordinal) &&
        string.Equals(result.ClientRequestId, input.ClientRequestId, StringComparison.Ordinal) &&
        string.Equals(result.ScopeId, input.ScopeId, StringComparison.Ordinal) &&
        string.Equals(
            result.ConversationActorId,
            input.ConversationActorId,
            StringComparison.Ordinal) &&
        string.Equals(result.TurnId, input.TurnId, StringComparison.Ordinal) &&
        string.Equals(result.TaskId, input.TaskId, StringComparison.Ordinal) &&
        string.Equals(result.StepId, input.StepId, StringComparison.Ordinal) &&
        result.ExpectedOperationGeneration == input.ExpectedOperationGeneration &&
        result.ExpectedStateVersion == input.ExpectedStateVersion;

    private static bool StepControlResultsEquivalent(
        NyxIdChatStepControlResultState left,
        NyxIdChatStepControlResultState right) =>
        left.ToByteString().Equals(right.ToByteString());

    private static bool TryResolveStepControlTarget(
        NyxIdChatConversationGAgentState state,
        StepControlInput input,
        out NyxIdChatTaskStepState step)
    {
        step = null!;
        if (state.ActiveTurn is null ||
            state.ActiveTask is null ||
            string.IsNullOrWhiteSpace(input.RequestId) ||
            !string.Equals(state.ScopeId, input.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(
                state.ConversationActorId,
                input.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTurn.TurnId, input.TurnId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTurn.TaskId, input.TaskId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTask.TurnId, input.TurnId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveTask.TaskId, input.TaskId, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = state.ActiveTask.Steps.FirstOrDefault(current =>
            string.Equals(current.StepId, input.StepId, StringComparison.Ordinal));
        if (candidate is null)
            return false;

        step = candidate;
        return true;
    }

    private static bool CanRedispatchRetry(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStepControlResultState result)
    {
        if (result.Kind != NyxIdChatStepControlKind.Retry ||
            result.Outcome != NyxIdChatTransitionOutcome.Accepted ||
            HasAcceptedFence(state) ||
            state.ActiveTask is null ||
            state.ActiveTurn?.Status != NyxIdChatTurnStatus.Active ||
            state.ActiveTask.Status != NyxIdChatTaskStatus.Active)
        {
            return false;
        }

        var step = state.ActiveTask.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, result.StepId, StringComparison.Ordinal));
        return step is
        {
            Kind: NyxIdChatStepKind.Llm,
            RetryInputRebuildable: true,
            Status: NyxIdChatStepStatus.Running,
            Operation.Phase: NyxIdChatOperationPhase.Requested,
        } &&
        step.Operation.Key.OperationGeneration == result.OperationGeneration;
    }

    private static NyxIdChatOperationDispatchCommand? BuildRetryDispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatRetryStepCommand command,
        long generation)
    {
        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, Normalize(command.StepId), StringComparison.Ordinal));
        if (step?.Operation?.Key is null ||
            step.Kind != NyxIdChatStepKind.Llm ||
            !step.RetryInputRebuildable ||
            step.Operation.Key.OperationGeneration != generation ||
            state.ActiveTurn is null)
        {
            return null;
        }

        var request = new ChatRequestEvent
        {
            Prompt = state.ActiveTurn.Prompt,
            SessionId = state.ActiveTurn.TurnId,
            ScopeId = state.ScopeId,
            CommandAttemptId = Normalize(command.CommandId),
            ToolContext = command.ToolContext?.Clone(),
            LlmControl = command.LlmControl?.Clone(),
        };
        request.InputParts.AddRange(state.ActiveTurn.InputParts.Select(static part => part.Clone()));
        return new NyxIdChatOperationDispatchCommand
        {
            Key = step.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = request,
                AgentProfile = state.ActiveTurn.AgentProfileTurnAuthority is null
                    ? null
                    : state.AgentProfile?.Clone(),
                AgentProfileTurnAuthority = state.ActiveTurn.AgentProfileTurnAuthority?.Clone(),
            },
        };
    }

    private static void ApplyTaskOutcomeAfterSkip(
        NyxIdChatConversationGAgentState state,
        Timestamp now)
    {
        var requiredFailure = state.ActiveTask.Steps.FirstOrDefault(step =>
            step.Required && step.Status is
                NyxIdChatStepStatus.Failed or
                NyxIdChatStepStatus.Cancelled or
                NyxIdChatStepStatus.Uncertain);
        if (requiredFailure is not null)
        {
            state.ActiveTask.Status = NyxIdChatTaskStatus.Failed;
            state.ActiveTurn.Status = NyxIdChatTurnStatus.Failed;
            state.ActiveTask.FailureCode = requiredFailure.FailureCode;
            state.ActiveTask.SafeMessage = requiredFailure.SafeMessage;
            state.ActiveTurn.FailureCode = requiredFailure.FailureCode;
            state.ActiveTurn.SafeMessage = requiredFailure.SafeMessage;
            state.ActiveTurn.TerminalAt ??= now.Clone();
        }
        else if (state.ActiveTask.Steps.Any(step => step.Status is
                     NyxIdChatStepStatus.Planned or
                     NyxIdChatStepStatus.Waiting or
                     NyxIdChatStepStatus.Running))
        {
            state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
            state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
            state.ActiveTask.FailureCode = string.Empty;
            state.ActiveTask.SafeMessage = string.Empty;
            state.ActiveTurn.FailureCode = string.Empty;
            state.ActiveTurn.SafeMessage = string.Empty;
            state.ActiveTurn.TerminalAt = null;
        }
        else
        {
            state.ActiveTask.Status = NyxIdChatTaskStatus.Succeeded;
            state.ActiveTurn.Status = NyxIdChatTurnStatus.Succeeded;
            state.ActiveTask.FailureCode = string.Empty;
            state.ActiveTask.SafeMessage = string.Empty;
            state.ActiveTurn.FailureCode = string.Empty;
            state.ActiveTurn.SafeMessage = string.Empty;
            state.ActiveTurn.TerminalAt = now.Clone();
        }

        state.ActiveTask.UpdatedAt = now.Clone();
        state.LatestTurn = state.ActiveTurn.Clone();
        if (IsTerminal(state.ActiveTurn.Status))
            AddTerminalSummary(state, state.ActiveTurn);
    }

    private static NyxIdChatConversationGAgentState ApplyTerminalFence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlFenceState fence,
        Timestamp now)
    {
        var next = state.Clone();
        next.ControlFence = fence.Clone();
        next.LatestControlResult = fence.Clone();
        next.PendingApproval = null;
        next.PendingInput = null;
        next.PendingInputRequest = null;

        var step = ResolveActiveStep(next);
        if (step is not null)
        {
            var physicallyInFlight = IsPhysicallyInFlight(step.Operation?.Phase ??
                                                          NyxIdChatOperationPhase.Unspecified);
            var uncertainEffect = physicallyInFlight &&
                                  step.Kind == NyxIdChatStepKind.Tool &&
                                  step.MayChangeExternalState;
            step.Status = uncertainEffect
                ? NyxIdChatStepStatus.Uncertain
                : NyxIdChatStepStatus.Cancelled;
            if (uncertainEffect)
            {
                step.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                step.FailureCode = "NYXID_CHAT_STOPPED_OUTCOME_UNCERTAIN";
                step.SafeMessage =
                    "The turn was stopped, but the external operation outcome may have changed.";
            }
            else
            {
                step.FailureCode = fence.ReasonCode;
                step.SafeMessage = fence.SafeMessage;
            }

            if (step.Operation is not null &&
                step.Operation.Phase == NyxIdChatOperationPhase.Requested)
            {
                step.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
                step.Operation.TerminalCode = fence.ReasonCode;
                step.Operation.SafeMessage = fence.SafeMessage;
                step.Operation.CompletedAt = now.Clone();
            }

            step.AvailableActions = new NyxIdChatAvailableActions();
            step.UpdatedAt = now.Clone();
        }

        next.ActiveTask.Status = NyxIdChatTaskStatus.Stopped;
        next.ActiveTask.ActiveStepId = string.Empty;
        next.ActiveTask.ActiveOperationId = string.Empty;
        next.ActiveTask.FailureCode = fence.ReasonCode;
        next.ActiveTask.SafeMessage = fence.SafeMessage;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Stopped;
        next.ActiveTurn.FailureCode = fence.ReasonCode;
        next.ActiveTurn.SafeMessage = fence.SafeMessage;
        next.ActiveTurn.TerminalAt = now.Clone();
        next.LatestTurn = next.ActiveTurn.Clone();
        AddTerminalSummary(next, next.ActiveTurn);
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return next;
    }

    private static NyxIdChatControlFenceState BuildResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlKind kind,
        string requestId,
        string clientRequestId,
        NyxIdChatControlOutcome outcome,
        string reasonCode,
        string safeMessage,
        Timestamp now) =>
        new()
        {
            Kind = kind,
            RequestId = requestId,
            ClientRequestId = Normalize(clientRequestId),
            TurnId = state.ActiveTurn?.TurnId ?? string.Empty,
            TaskId = state.ActiveTask?.TaskId ?? string.Empty,
            OperationGeneration = ResolveOperationGeneration(state),
            Outcome = outcome,
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
            CommittedAt = now.Clone(),
        };

    private static bool MatchesControlIdentity(
        NyxIdChatConversationGAgentState state,
        string scopeId,
        string conversationActorId,
        string turnId,
        string requestId) =>
        state.ActiveTurn is not null &&
        state.ActiveTask is not null &&
        !string.IsNullOrWhiteSpace(requestId) &&
        string.Equals(state.ScopeId, Normalize(scopeId), StringComparison.Ordinal) &&
        string.Equals(
            state.ConversationActorId,
            Normalize(conversationActorId),
            StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn.TurnId, Normalize(turnId), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTask.TurnId, Normalize(turnId), StringComparison.Ordinal);

    private static bool MatchesExpectedVersion(long expected, long actual) =>
        expected <= 0 || expected == actual;

    private static NyxIdChatControlFenceState? FindExactControlReplay(
        NyxIdChatConversationGAgentState state,
        NyxIdChatControlKind kind,
        string requestId,
        string clientRequestId,
        string turnId)
    {
        if (MatchesControlReplay(
                state.LatestControlResult,
                kind,
                requestId,
                clientRequestId,
                turnId))
        {
            return state.LatestControlResult;
        }

        return MatchesControlReplay(
            state.ControlFence,
            kind,
            requestId,
            clientRequestId,
            turnId)
            ? state.ControlFence
            : null;
    }

    private static bool MatchesControlReplay(
        NyxIdChatControlFenceState? result,
        NyxIdChatControlKind kind,
        string requestId,
        string clientRequestId,
        string turnId) =>
        result is not null &&
        result.Kind == kind &&
        string.Equals(result.RequestId, requestId, StringComparison.Ordinal) &&
        string.Equals(result.ClientRequestId, clientRequestId, StringComparison.Ordinal) &&
        string.Equals(result.TurnId, turnId, StringComparison.Ordinal);

    private static bool IsExactSteeringReplay(
        NyxIdChatConversationGAgentState state,
        NyxIdChatSteeringCommand command,
        string requestId)
    {
        var admission = state.ContinuationAdmission;
        return admission is not null &&
               admission.Kind == NyxIdChatContinuationKind.Steering &&
               string.Equals(admission.RequestId, requestId, StringComparison.Ordinal) &&
               string.Equals(
                   admission.ClientRequestId,
                   Normalize(command.ClientRequestId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   admission.OriginTurnId,
                   Normalize(command.TurnId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   admission.Instruction,
                   command.Instruction?.Trim(),
                   StringComparison.Ordinal) &&
               admission.InputParts.Select(static part => part.ToByteString())
                   .SequenceEqual(command.InputParts.Select(SanitizeInputPart)
                       .Select(static part => part.ToByteString()));
    }

    private static bool HasAcceptedFence(NyxIdChatConversationGAgentState state) =>
        state.ControlFence is
        {
            Outcome: NyxIdChatControlOutcome.Accepted or
                NyxIdChatControlOutcome.Uncancellable,
        };

    private static bool HasPhysicallyInFlightOperation(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask?.Steps.Any(step =>
            IsPhysicallyInFlight(step.Operation?.Phase ??
                                 NyxIdChatOperationPhase.Unspecified)) == true;

    private static bool IsPhysicallyInFlight(NyxIdChatOperationPhase phase) =>
        phase is NyxIdChatOperationPhase.Dispatched or NyxIdChatOperationPhase.Running;

    private static bool IsTerminal(NyxIdChatOperationPhase phase) =>
        phase is NyxIdChatOperationPhase.Succeeded or
            NyxIdChatOperationPhase.Failed or
            NyxIdChatOperationPhase.Cancelled or
            NyxIdChatOperationPhase.Uncertain;

    private static bool HasAcceptedFenceFor(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey? key) =>
        key is not null &&
        state.ControlFence is
        {
            Outcome: NyxIdChatControlOutcome.Accepted or
                NyxIdChatControlOutcome.Uncancellable,
        } fence &&
        state.ActiveTask is not null &&
        state.ActiveTurn is not null &&
        state.ActiveTask.Status == NyxIdChatTaskStatus.Stopped &&
        state.ActiveTurn.Status == NyxIdChatTurnStatus.Stopped &&
        string.Equals(state.ConversationActorId, key.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(fence.TurnId, key.TurnId, StringComparison.Ordinal) &&
        string.Equals(fence.TaskId, key.TaskId, StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn.TurnId, key.TurnId, StringComparison.Ordinal) &&
        string.Equals(state.ActiveTask.TaskId, key.TaskId, StringComparison.Ordinal);

    private static bool TryResolveStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey? key,
        out NyxIdChatTaskStepState step)
    {
        step = null!;
        if (key is null || state.ActiveTask is null)
            return false;

        var candidate = state.ActiveTask.Steps.FirstOrDefault(current =>
            KeysEqual(current.Operation?.Key, key));
        if (candidate is null)
            return false;

        step = candidate;
        return true;
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

    private static LateToolEvidence? ClassifyLateOperationEvidence(
        NyxIdChatTaskStepState step,
        NyxIdChatOperationResultSignal signal)
    {
        if (step.Kind == NyxIdChatStepKind.Llm &&
            signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm)
        {
            return new LateToolEvidence(
                NyxIdChatOperationPhase.Succeeded,
                NyxIdChatEffectEvidence.NotApplied,
                string.Empty,
                string.Empty);
        }

        if (step.Kind != NyxIdChatStepKind.Tool ||
            signal.ResultCase != NyxIdChatOperationResultSignal.ResultOneofCase.Tool)
        {
            return null;
        }

        var result = signal.Tool;
        var receipt = result.Receipt;
        if (receipt is null)
            return null;

        var terminalCode = receipt.ErrorCode ?? string.Empty;
        var safeMessage = receipt.ErrorMessage ?? string.Empty;
        return receipt.Status switch
        {
            AgentToolReceiptStatus.Success => new LateToolEvidence(
                NyxIdChatOperationPhase.Succeeded,
                step.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.Confirmed
                    : NyxIdChatEffectEvidence.NotApplied,
                terminalCode,
                safeMessage),
            AgentToolReceiptStatus.ApprovalRequired or
                AgentToolReceiptStatus.AuthorizationRequired => new LateToolEvidence(
                    NyxIdChatOperationPhase.Cancelled,
                    NyxIdChatEffectEvidence.NotStarted,
                    terminalCode,
                    safeMessage),
            AgentToolReceiptStatus.Denied => new LateToolEvidence(
                NyxIdChatOperationPhase.Cancelled,
                NyxIdChatEffectEvidence.NotApplied,
                terminalCode,
                safeMessage),
            AgentToolReceiptStatus.Error => ClassifyLateFailure(
                step,
                result.ExternalEffect,
                terminalCode,
                safeMessage),
            _ => null,
        };
    }

    private static LateToolEvidence ClassifyLateFailure(
        NyxIdChatTaskStepState step,
        NyxIdChatEffectEvidence reportedEffect,
        string terminalCode,
        string safeMessage)
    {
        var effect = reportedEffect switch
        {
            NyxIdChatEffectEvidence.NotStarted => NyxIdChatEffectEvidence.NotStarted,
            NyxIdChatEffectEvidence.NotApplied => NyxIdChatEffectEvidence.NotApplied,
            NyxIdChatEffectEvidence.Confirmed => NyxIdChatEffectEvidence.Confirmed,
            NyxIdChatEffectEvidence.MayHaveChanged => NyxIdChatEffectEvidence.MayHaveChanged,
            _ => step.MayChangeExternalState
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied,
        };
        return new LateToolEvidence(
            effect == NyxIdChatEffectEvidence.MayHaveChanged
                ? NyxIdChatOperationPhase.Uncertain
                : NyxIdChatOperationPhase.Failed,
            effect,
            terminalCode,
            safeMessage);
    }

    private static NyxIdChatTaskStepState? ResolveActiveStep(
        NyxIdChatConversationGAgentState state)
    {
        if (state.ActiveTask is null)
            return null;
        var active = state.ActiveTask.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, state.ActiveTask.ActiveStepId, StringComparison.Ordinal));
        return active ?? state.ActiveTask.Steps.LastOrDefault(step =>
            step.Status is NyxIdChatStepStatus.Planned or
                NyxIdChatStepStatus.Waiting or
                NyxIdChatStepStatus.Running);
    }

    private static long ResolveOperationGeneration(NyxIdChatConversationGAgentState state) =>
        ResolveActiveStep(state)?.Operation?.Key?.OperationGeneration ?? 0;

    private static bool IsTerminal(NyxIdChatTurnStatus status) =>
        status is NyxIdChatTurnStatus.Succeeded or
            NyxIdChatTurnStatus.Failed or
            NyxIdChatTurnStatus.Stopped or
            NyxIdChatTurnStatus.Blocked;

    private static void AddTerminalSummary(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        const int historyLimit = 32;
        var summary = state.RecentTerminalTurns.FirstOrDefault(candidate =>
            string.Equals(candidate.TurnId, turn.TurnId, StringComparison.Ordinal));
        if (summary is null)
        {
            summary = new NyxIdChatTurnSummary();
            state.RecentTerminalTurns.Add(summary);
        }

        summary.TurnId = turn.TurnId;
        summary.TaskId = turn.TaskId;
        summary.Status = turn.Status;
        summary.FailureCode = turn.FailureCode;
        summary.SafeMessage = turn.SafeMessage;
        summary.TerminalAt = turn.TerminalAt?.Clone();
        while (state.RecentTerminalTurns.Count > historyLimit)
            state.RecentTerminalTurns.RemoveAt(0);
    }

    private static Aevatar.AI.Abstractions.ChatContentPart SanitizeInputPart(
        Aevatar.AI.Abstractions.ChatContentPart source)
    {
        var safe = source.Clone();
        safe.DataBase64 = string.Empty;
        return safe;
    }

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private readonly record struct StepControlInput(
        NyxIdChatStepControlKind Kind,
        string RequestId,
        string ClientRequestId,
        string ScopeId,
        string ConversationActorId,
        string TurnId,
        string TaskId,
        string StepId,
        long ExpectedOperationGeneration,
        long ExpectedStateVersion,
        string CommandId,
        string CorrelationId)
    {
        public static StepControlInput From(NyxIdChatRetryStepCommand command) =>
            new(
                NyxIdChatStepControlKind.Retry,
                Normalize(command.RetryRequestId),
                Normalize(command.ClientRequestId),
                Normalize(command.ScopeId),
                Normalize(command.ConversationActorId),
                Normalize(command.TurnId),
                Normalize(command.TaskId),
                Normalize(command.StepId),
                command.ExpectedOperationGeneration,
                command.ExpectedStateVersion,
                Normalize(command.CommandId),
                Normalize(command.CorrelationId));

        public static StepControlInput From(NyxIdChatSkipStepCommand command) =>
            new(
                NyxIdChatStepControlKind.Skip,
                Normalize(command.SkipRequestId),
                Normalize(command.ClientRequestId),
                Normalize(command.ScopeId),
                Normalize(command.ConversationActorId),
                Normalize(command.TurnId),
                Normalize(command.TaskId),
                Normalize(command.StepId),
                command.ExpectedOperationGeneration,
                command.ExpectedStateVersion,
                Normalize(command.CommandId),
                Normalize(command.CorrelationId));
    }

    private readonly record struct LateToolEvidence(
        NyxIdChatOperationPhase Phase,
        NyxIdChatEffectEvidence Effect,
        string TerminalCode,
        string SafeMessage);
}
