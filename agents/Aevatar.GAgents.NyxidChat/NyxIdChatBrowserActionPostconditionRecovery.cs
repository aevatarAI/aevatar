using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal enum NyxIdChatBrowserActionPostconditionRecoveryStatus
{
    NotApplicable,
    Ready,
    UpgradeRequired,
    CompletedUpgradeRequired,
    Invalid,
}

internal sealed record NyxIdChatBrowserActionPostconditionRecoveryDecision(
    NyxIdChatBrowserActionPostconditionRecoveryStatus Status,
    NyxIdChatConversationGAgentState State,
    NyxIdChatOperationDispatchCommand? Command,
    NyxIdChatOperationKey? OriginalKey,
    NyxIdAssistantActionKind Action);

/// <summary>
/// Upgrades only the historical browser-action postcondition contract. Every
/// dispatch input is rebuilt from the exact actor-owned request and accepted
/// continuation admission; unbound result payloads are never retained as
/// verification evidence.
/// </summary>
internal static class NyxIdChatBrowserActionPostconditionRecovery
{
    internal static NyxIdChatBrowserActionPostconditionRecoveryDecision
        BuildRequestedRecovery(
            NyxIdChatConversationGAgentState state,
            NyxIdChatOperationKey key,
            Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectInFlight(state, key, requireRequestedPhase: true);
        if (inspection.Status != NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state);
        }

        var context = inspection.Context;
        if (!context.LegacyContract)
        {
            return new NyxIdChatBrowserActionPostconditionRecoveryDecision(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
                state.Clone(),
                BuildCommand(state, context, key),
                key.Clone(),
                context.Request.Action);
        }

        var upgraded = UpgradeBinding(state, key, context.Request.Action, now);
        var upgradedContext = RequireInFlightContext(upgraded, key);
        return new NyxIdChatBrowserActionPostconditionRecoveryDecision(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired,
            upgraded,
            BuildCommand(upgraded, upgradedContext, key),
            key.Clone(),
            context.Request.Action);
    }

    internal static NyxIdChatBrowserActionPostconditionRecoveryDecision BuildInFlightUpgrade(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectInFlight(state, key, requireRequestedPhase: false);
        if (inspection.Status != NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state);
        }

        var context = inspection.Context;
        return context.LegacyContract
            ? new NyxIdChatBrowserActionPostconditionRecoveryDecision(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired,
                UpgradeBinding(state, key, context.Request.Action, now),
                null,
                key.Clone(),
                context.Request.Action)
            : new NyxIdChatBrowserActionPostconditionRecoveryDecision(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
                state.Clone(),
                null,
                key.Clone(),
                context.Request.Action);
    }

    internal static NyxIdChatBrowserActionPostconditionRecoveryDecision BuildFreshRedispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal legacyResult,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(now);

        if (legacyResult.Key is null ||
            legacyResult.ResultCase !=
            NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition ||
            legacyResult.ActionPostcondition.VerificationInputSha256.Length != 0)
        {
            return Decision(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.NotApplicable,
                state);
        }

        var inspection = InspectInFlight(
            state,
            legacyResult.Key,
            requireRequestedPhase: false);
        if (inspection.Status != NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state);
        }

        var context = inspection.Context;
        if (legacyResult.Key.OperationGeneration != 1)
        {
            return Decision(NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid, state);
        }

        if (!string.Equals(
                legacyResult.ActionPostcondition.ActionRequestId,
                context.Request.ActionRequestId,
                StringComparison.Ordinal))
        {
            return Decision(NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid, state);
        }

        var upgraded = UpgradeBinding(
            state,
            legacyResult.Key,
            context.Request.Action,
            now);
        if (KeysEqual(upgraded.PendingOperationDeliveryProbe, legacyResult.Key))
            upgraded.PendingOperationDeliveryProbe = null;
        var step = FindExactStep(upgraded, legacyResult.Key)!;
        var generation = checked(legacyResult.Key.OperationGeneration + 1);
        var redispatchKey = BuildOperationKey(
            upgraded.ConversationActorId,
            legacyResult.Key.TurnId,
            legacyResult.Key.TaskId,
            step.StepId,
            generation);
        step.Status = NyxIdChatStepStatus.Running;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        step.FailureCode = string.Empty;
        step.SafeMessage = string.Empty;
        step.Operation = new NyxIdChatOperationState
        {
            Key = redispatchKey.Clone(),
            Kind = NyxIdChatStepKind.Postcondition,
            Phase = NyxIdChatOperationPhase.Requested,
            RequestedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        step.UpdatedAt = now.Clone();
        upgraded.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        upgraded.ActiveTask.ActiveStepId = step.StepId;
        upgraded.ActiveTask.ActiveOperationId = redispatchKey.OperationId;
        upgraded.ActiveTask.FailureCode = string.Empty;
        upgraded.ActiveTask.SafeMessage = string.Empty;
        upgraded.ActiveTask.UpdatedAt = now.Clone();
        upgraded.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        upgraded.ActiveTurn.FailureCode = string.Empty;
        upgraded.ActiveTurn.SafeMessage = string.Empty;
        upgraded.ActiveTurn.TerminalAt = null;
        upgraded.LatestTurn = upgraded.ActiveTurn.Clone();
        upgraded.UpdatedAt = now.Clone();

        var upgradedContext = RequireInFlightContext(upgraded, redispatchKey);
        return new NyxIdChatBrowserActionPostconditionRecoveryDecision(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired,
            upgraded,
            BuildCommand(upgraded, upgradedContext, redispatchKey),
            legacyResult.Key.Clone(),
            context.Request.Action);
    }

    internal static NyxIdChatBrowserActionPostconditionRecoveryDecision BuildCompletedUpgrade(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey continuationKey,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(continuationKey);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectCompleted(state, continuationKey);
        if (inspection.Status != NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state);
        }

        var context = inspection.Context;
        if (!context.LegacyContract)
        {
            return new NyxIdChatBrowserActionPostconditionRecoveryDecision(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
                state.Clone(),
                null,
                context.PostconditionStep.Operation.Key.Clone(),
                context.Request.Action);
        }

        return new NyxIdChatBrowserActionPostconditionRecoveryDecision(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.CompletedUpgradeRequired,
            UpgradeBinding(
                state,
                context.PostconditionStep.Operation.Key,
                context.Request.Action,
                now),
            null,
            context.PostconditionStep.Operation.Key.Clone(),
            context.Request.Action);
    }

    internal static bool HasVerifiedCompletedBoundState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey continuationKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(continuationKey);
        var inspection = InspectCompleted(state, continuationKey);
        return inspection is
        {
            Status: NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
            Context.LegacyContract: false,
        };
    }

    internal static bool TryResolveVerifiedCompletedBoundAction(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey postconditionKey,
        out NyxIdAssistantActionKind action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(postconditionKey);
        action = NyxIdAssistantActionKind.Unspecified;

        var task = state.ActiveTask;
        var postconditions = task?.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, postconditionKey))
            .Take(2)
            .ToArray() ?? [];
        if (postconditions.Length != 1)
            return false;

        var postcondition = postconditions[0];
        var actionRequestId = postcondition.Source?.Postcondition?.ActionRequestId;
        var requests = state.RecentActions
            .Where(candidate => string.Equals(
                candidate.ActionRequestId,
                actionRequestId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (requests.Length != 1 ||
            !HasVerifiedCompletedBoundEvidence(state, postcondition, requests[0]))
            return false;

        action = requests[0].Action;
        return true;
    }

    internal static bool HasVerifiedCompletedBoundEvidence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState postcondition,
        NyxIdChatActionRequestState request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(postcondition);
        ArgumentNullException.ThrowIfNull(request);

        var task = state.ActiveTask;
        var source = postcondition.Source?.Postcondition;
        var result = request.PostconditionResult;
        if (task is null ||
            source is null ||
            result is null ||
            source.VerificationInputBinding !=
            NyxIdChatVerificationInputBinding.Sha256V1 ||
            source.Action != request.Action ||
            !ValidateRequest(state, request, requireRecoverableAction: false) ||
            !TryResolveAdmission(state, request, out var admission, out var report))
        {
            return false;
        }

        var expectedStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            request.TaskId,
            request.ActionRequestId,
            "postcondition");
        var expectedKey = BuildOperationKey(
            state.ConversationActorId,
            request.OriginTurnId,
            request.TaskId,
            expectedStepId,
            postcondition.Operation?.Key?.OperationGeneration ?? 0);
        var sourceToolValid = HasValidCompletedSourceTool(task, request);
        var expectedInput = NyxIdChatBrowserActions.BuildPostconditionInput(
            state.ScopeId,
            admission.OwnerSubject,
            request,
            report);
        return state.RecentActions.Count(candidate => string.Equals(
                   candidate.ActionRequestId,
                   request.ActionRequestId,
                   StringComparison.Ordinal)) == 1 &&
               state.PendingActions.All(candidate => !string.Equals(
                   candidate.ActionRequestId,
                   request.ActionRequestId,
                   StringComparison.Ordinal)) &&
               postcondition.Kind == NyxIdChatStepKind.Postcondition &&
               postcondition.Status == NyxIdChatStepStatus.Done &&
               postcondition.Required &&
               postcondition.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
               string.IsNullOrEmpty(postcondition.FailureCode) &&
               postcondition.Operation is
               {
                   Kind: NyxIdChatStepKind.Postcondition,
                   Phase: NyxIdChatOperationPhase.Succeeded,
                   RequestedAt: not null,
                   CompletedAt: not null,
               } &&
               string.IsNullOrEmpty(postcondition.Operation.TerminalCode) &&
               postcondition.DependsOn.Count == 1 &&
               string.Equals(postcondition.DependsOn[0], request.StepId,
                   StringComparison.Ordinal) &&
               sourceToolValid &&
               string.Equals(postcondition.StepId, expectedStepId,
                   StringComparison.Ordinal) &&
               string.Equals(postcondition.ActionRequestId, request.ActionRequestId,
                   StringComparison.Ordinal) &&
               KeysEqual(postcondition.Operation.Key, expectedKey) &&
               string.Equals(source.ActionRequestId, request.ActionRequestId,
                   StringComparison.Ordinal) &&
               string.Equals(source.Check, request.Action.ToString(),
                   StringComparison.Ordinal) &&
               source.ToolReadBack is null &&
               string.IsNullOrEmpty(source.EffectStepId) &&
               string.IsNullOrEmpty(source.ProviderResourceId) &&
               result.Verified &&
               result.Disposition == NyxIdChatActionDisposition.Completed &&
               string.Equals(result.ActionRequestId, request.ActionRequestId,
                   StringComparison.Ordinal) &&
               NyxIdChatBrowserActions.PostconditionResourceMatchesAction(
                   request.Action,
                   result) &&
               NyxIdChatActionPostconditionEvidence.Matches(expectedInput, result) &&
               ReportMatchesRequest(request, admission, report);
    }

    private static Inspection InspectInFlight(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        bool requireRequestedPhase)
    {
        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        if (task is null || turn is null)
            return Inspection.NotApplicable;

        var matchingSteps = task.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, key))
            .Take(2)
            .ToArray();
        if (matchingSteps.Length != 1)
        {
            return HasBrowserHint(state, key)
                ? Inspection.Invalid
                : Inspection.NotApplicable;
        }

        var step = matchingSteps[0];
        var source = step.Source?.Postcondition;
        if (step.Kind != NyxIdChatStepKind.Postcondition || source is null)
            return Inspection.NotApplicable;

        var requests = state.PendingActions
            .Where(candidate =>
                string.Equals(candidate.ActionRequestId, source.ActionRequestId,
                    StringComparison.Ordinal) ||
                string.Equals(candidate.ActionRequestId, step.ActionRequestId,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (requests.Length == 0 && !IsRecoverableAction(source.Action))
            return Inspection.NotApplicable;
        if (requests.Length != 1)
            return Inspection.Invalid;

        var request = requests[0];
        if (!IsRecoverableAction(request.Action))
            return Inspection.NotApplicable;
        if (!TryResolveAdmission(state, request, out var admission, out var report))
            return Inspection.Invalid;

        var legacyContract =
            source.VerificationInputBinding == NyxIdChatVerificationInputBinding.Unspecified &&
            (source.Action == NyxIdAssistantActionKind.Unspecified ||
             source.Action == request.Action);
        var boundContract =
            source.VerificationInputBinding == NyxIdChatVerificationInputBinding.Sha256V1 &&
            source.Action == request.Action;
        var phaseValid = requireRequestedPhase
            ? step.Operation?.Phase == NyxIdChatOperationPhase.Requested
            : step.Operation?.Phase is NyxIdChatOperationPhase.Requested or
                NyxIdChatOperationPhase.Dispatched or
                NyxIdChatOperationPhase.Running;
        var expectedStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            request.ActionRequestId,
            "postcondition");
        var expectedKey = BuildOperationKey(
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            expectedStepId,
            key.OperationGeneration);
        var actionSteps = task.Steps
            .Where(candidate => string.Equals(
                candidate.StepId,
                request.StepId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var actionStep = actionSteps.Length == 1 ? actionSteps[0] : null;
        var sourceSteps = request.HasSourceToolStepId
            ? task.Steps.Where(candidate => string.Equals(
                    candidate.StepId,
                    request.SourceToolStepId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray()
            : [];
        var sourceStep = sourceSteps.Length == 1 ? sourceSteps[0] : null;
        var valid =
            (legacyContract || boundContract) &&
            (!legacyContract || key.OperationGeneration == 1) &&
            ValidateRequest(state, request) &&
            state.ControlFence is null &&
            state.RecentActions.All(candidate => !string.Equals(
                candidate.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal)) &&
            request.PostconditionResult is null &&
            task.Status == NyxIdChatTaskStatus.Active &&
            turn.Status == NyxIdChatTurnStatus.Active &&
            string.Equals(task.TaskId, key.TaskId, StringComparison.Ordinal) &&
            string.Equals(task.TaskId, request.TaskId, StringComparison.Ordinal) &&
            string.Equals(task.TurnId, turn.TurnId, StringComparison.Ordinal) &&
            string.Equals(turn.TaskId, task.TaskId, StringComparison.Ordinal) &&
            string.Equals(turn.TurnId, admission.ContinuationTurnId,
                StringComparison.Ordinal) &&
            string.Equals(task.ActiveStepId, step.StepId, StringComparison.Ordinal) &&
            string.Equals(task.ActiveOperationId, key.OperationId,
                StringComparison.Ordinal) &&
            step.Status == NyxIdChatStepStatus.Running &&
            step.Required &&
            step.ExternalEffect == NyxIdChatEffectEvidence.NotStarted &&
            string.IsNullOrEmpty(step.FailureCode) &&
            step.Operation is
            {
                Kind: NyxIdChatStepKind.Postcondition,
                RequestedAt: not null,
                CompletedAt: null,
            } &&
            phaseValid &&
            source.ToolReadBack is null &&
            string.IsNullOrEmpty(source.EffectStepId) &&
            string.IsNullOrEmpty(source.ProviderResourceId) &&
            string.Equals(source.ActionRequestId, request.ActionRequestId,
                StringComparison.Ordinal) &&
            string.Equals(source.Check, request.Action.ToString(), StringComparison.Ordinal) &&
            string.Equals(step.ActionRequestId, request.ActionRequestId,
                StringComparison.Ordinal) &&
            step.DependsOn.Count == 1 &&
            string.Equals(step.DependsOn[0], request.StepId, StringComparison.Ordinal) &&
            string.Equals(step.StepId, expectedStepId, StringComparison.Ordinal) &&
            KeysEqual(key, expectedKey) &&
            task.Steps.Count(candidate => string.Equals(
                candidate.StepId,
                step.StepId,
                StringComparison.Ordinal)) == 1 &&
            actionStep is
            {
                Kind: NyxIdChatStepKind.BrowserAction,
                Status: NyxIdChatStepStatus.Waiting,
                ExternalEffect: NyxIdChatEffectEvidence.NotStarted,
            } &&
            actionStep.Source?.BrowserAction?.Action == request.Action &&
            string.Equals(actionStep.Source.BrowserAction.ActionRequestId,
                request.ActionRequestId, StringComparison.Ordinal) &&
            string.Equals(actionStep.ActionRequestId, request.ActionRequestId,
                StringComparison.Ordinal) &&
            request.HasSourceToolStepId &&
            sourceStep is
            {
                Kind: NyxIdChatStepKind.Tool,
                Status: NyxIdChatStepStatus.Waiting,
                Operation.Key: not null,
            } &&
            string.Equals(sourceStep.Operation.Key.ConversationActorId,
                state.ConversationActorId, StringComparison.Ordinal) &&
            string.Equals(sourceStep.Operation.Key.TurnId, request.OriginTurnId,
                StringComparison.Ordinal) &&
            string.Equals(sourceStep.Operation.Key.TaskId, task.TaskId,
                StringComparison.Ordinal) &&
            ReportMatchesRequest(request, admission, report);

        return valid
            ? new Inspection(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
                new RecoveryContext(step, request, admission, report, legacyContract))
            : Inspection.Invalid;
    }

    private static Inspection InspectCompleted(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey continuationKey)
    {
        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        if (task is null || turn is null)
            return Inspection.NotApplicable;

        var continuationSteps = task.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, continuationKey))
            .Take(2)
            .ToArray();
        if (continuationSteps.Length != 1)
            return Inspection.NotApplicable;
        var continuation = continuationSteps[0];
        var actionRequestId = continuation.Source?.Llm?.ActionRequestId;
        if (continuation.Kind != NyxIdChatStepKind.Llm ||
            string.IsNullOrWhiteSpace(actionRequestId))
        {
            return Inspection.NotApplicable;
        }

        var requests = state.RecentActions
            .Where(candidate => string.Equals(
                candidate.ActionRequestId,
                actionRequestId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (requests.Length != 1)
            return Inspection.Invalid;
        var request = requests[0];
        if (!TryResolveAdmission(state, request, out var admission, out var report))
            return Inspection.Invalid;

        var postconditionSteps = task.Steps
            .Where(candidate =>
                candidate.Kind == NyxIdChatStepKind.Postcondition &&
                string.Equals(candidate.ActionRequestId, actionRequestId,
                    StringComparison.Ordinal) &&
                string.Equals(candidate.Source?.Postcondition?.ActionRequestId,
                    actionRequestId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (postconditionSteps.Length != 1)
            return Inspection.Invalid;
        var postcondition = postconditionSteps[0];
        var source = postcondition.Source.Postcondition;
        var legacyContract =
            source.VerificationInputBinding == NyxIdChatVerificationInputBinding.Unspecified &&
            (source.Action == NyxIdAssistantActionKind.Unspecified ||
             source.Action == request.Action);
        var boundContract =
            source.VerificationInputBinding == NyxIdChatVerificationInputBinding.Sha256V1 &&
            source.Action == request.Action;
        if (legacyContract && !IsRecoverableAction(request.Action))
            return Inspection.Invalid;
        var expectedPostconditionStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            request.ActionRequestId,
            "postcondition");
        var expectedPostconditionKey = BuildOperationKey(
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            expectedPostconditionStepId,
            postcondition.Operation?.Key?.OperationGeneration ?? 0);
        var expectedContinuationStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            request.ActionRequestId,
            "llm-continuation");
        var expectedContinuationKey = BuildOperationKey(
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            expectedContinuationStepId,
            1);
        var result = request.PostconditionResult;
        var expectedInput = NyxIdChatBrowserActions.BuildPostconditionInput(
            state.ScopeId,
            admission.OwnerSubject,
            request,
            report);
        var actionSteps = task.Steps
            .Where(candidate => string.Equals(candidate.StepId, request.StepId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var actionStep = actionSteps.Length == 1 ? actionSteps[0] : null;
        var sourceToolValid = HasValidCompletedSourceTool(task, request);
        var valid =
            (legacyContract || boundContract) &&
            (!legacyContract ||
             postcondition.Operation?.Key?.OperationGeneration == 1) &&
            ValidateRequest(state, request, requireRecoverableAction: legacyContract) &&
            state.ControlFence is null &&
            state.PendingActions.All(candidate => !string.Equals(
                candidate.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal)) &&
            task.Status == NyxIdChatTaskStatus.Active &&
            turn.Status == NyxIdChatTurnStatus.Active &&
            string.Equals(task.TaskId, continuationKey.TaskId, StringComparison.Ordinal) &&
            string.Equals(task.TurnId, turn.TurnId, StringComparison.Ordinal) &&
            string.Equals(turn.TaskId, task.TaskId, StringComparison.Ordinal) &&
            string.Equals(turn.TurnId, admission.ContinuationTurnId,
                StringComparison.Ordinal) &&
            string.Equals(task.ActiveStepId, continuation.StepId,
                StringComparison.Ordinal) &&
            string.Equals(task.ActiveOperationId, continuationKey.OperationId,
                StringComparison.Ordinal) &&
            continuation.Status == NyxIdChatStepStatus.Running &&
            continuation.Required &&
            continuation.ExternalEffect == NyxIdChatEffectEvidence.NotStarted &&
            continuation.Operation is
            {
                Kind: NyxIdChatStepKind.Llm,
                RequestedAt: not null,
                CompletedAt: null,
                Phase: NyxIdChatOperationPhase.Requested or
                    NyxIdChatOperationPhase.Dispatched or
                    NyxIdChatOperationPhase.Running,
            } &&
            continuation.DependsOn.Count == 1 &&
            string.Equals(continuation.DependsOn[0], postcondition.StepId,
                StringComparison.Ordinal) &&
            string.Equals(continuation.StepId, expectedContinuationStepId,
                StringComparison.Ordinal) &&
            KeysEqual(continuationKey, expectedContinuationKey) &&
            postcondition.Status == NyxIdChatStepStatus.Done &&
            postcondition.Required &&
            postcondition.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
            string.IsNullOrEmpty(postcondition.FailureCode) &&
            postcondition.Operation is
            {
                Kind: NyxIdChatStepKind.Postcondition,
                Phase: NyxIdChatOperationPhase.Succeeded,
                RequestedAt: not null,
                CompletedAt: not null,
            } &&
            string.IsNullOrEmpty(postcondition.Operation.TerminalCode) &&
            postcondition.DependsOn.Count == 1 &&
            string.Equals(postcondition.DependsOn[0], request.StepId,
                StringComparison.Ordinal) &&
            string.Equals(postcondition.StepId, expectedPostconditionStepId,
                StringComparison.Ordinal) &&
            KeysEqual(postcondition.Operation.Key, expectedPostconditionKey) &&
            string.Equals(source.ActionRequestId, request.ActionRequestId,
                StringComparison.Ordinal) &&
            string.Equals(source.Check, request.Action.ToString(), StringComparison.Ordinal) &&
            source.ToolReadBack is null &&
            string.IsNullOrEmpty(source.EffectStepId) &&
            string.IsNullOrEmpty(source.ProviderResourceId) &&
            result is
            {
                Verified: true,
                Disposition: NyxIdChatActionDisposition.Completed,
            } &&
            string.Equals(result.ActionRequestId, request.ActionRequestId,
                StringComparison.Ordinal) &&
            NyxIdChatBrowserActions.PostconditionResourceMatchesAction(request.Action, result) &&
            NyxIdChatActionPostconditionEvidence.Matches(expectedInput, result) &&
            actionStep is
            {
                Kind: NyxIdChatStepKind.BrowserAction,
                Status: NyxIdChatStepStatus.Done,
                ExternalEffect: NyxIdChatEffectEvidence.Confirmed,
            } &&
            actionStep.Source?.BrowserAction?.Action == request.Action &&
            string.Equals(actionStep.Source.BrowserAction.ActionRequestId,
                request.ActionRequestId, StringComparison.Ordinal) &&
            actionStep.DependsOn.Count == 1 &&
            string.Equals(actionStep.DependsOn[0], request.SourceToolStepId,
                StringComparison.Ordinal) &&
            sourceToolValid &&
            ReportMatchesRequest(request, admission, report);

        return valid
            ? new Inspection(
                NyxIdChatBrowserActionPostconditionRecoveryStatus.Ready,
                new RecoveryContext(postcondition, request, admission, report,
                    legacyContract))
            : Inspection.Invalid;
    }

    private static bool TryResolveAdmission(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request,
        out NyxIdChatContinuationAdmissionState admission,
        out NyxIdChatActionReport? report)
    {
        admission = state.ContinuationAdmission!;
        report = null;
        if (admission is not
            {
                Kind: NyxIdChatContinuationKind.Action,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
                CommittedAt: not null,
            } ||
            !IsNormalizedRequired(admission.RequestId) ||
            !IsNormalizedRequired(admission.ClientRequestId) ||
            !IsNormalizedRequired(admission.ContinuationTurnId) ||
            !IsNormalizedRequired(admission.OwnerSubject) ||
            !string.Equals(admission.ReasonCode,
                NyxIdChatBrowserActions.ActionContinuationAccepted,
                StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(admission.SafeMessage))
        {
            return false;
        }

        var reports = admission.ActionReports
            .Where(candidate => string.Equals(
                candidate.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (reports.Length > 1)
            return false;
        if (reports.Length == 1)
        {
            report = reports[0];
            return report.Disposition == NyxIdChatActionDisposition.Completed &&
                   string.Equals(admission.OriginTurnId, request.OriginTurnId,
                       StringComparison.Ordinal) &&
                   string.Equals(report.OriginTurnId, request.OriginTurnId,
                       StringComparison.Ordinal) &&
                   NyxIdChatBrowserActions.ResourceMatchesAction(
                       request.Action,
                       report.Disposition,
                       report.Resource);
        }

        return admission.ActionReports.Count == 0 &&
               string.IsNullOrEmpty(admission.OriginTurnId);
    }

    private static bool ReportMatchesRequest(
        NyxIdChatActionRequestState request,
        NyxIdChatContinuationAdmissionState admission,
        NyxIdChatActionReport? report)
    {
        if (report is null)
            return admission.ActionReports.Count == 0 && request.Reports.Count == 0;

        var requestReports = request.Reports
            .Where(candidate => string.Equals(
                candidate.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return requestReports.Length == 1 &&
               NyxIdChatBrowserActions.ReportsEqual(requestReports[0], report);
    }

    private static bool ValidateRequest(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request,
        bool requireRecoverableAction = true)
    {
        if (!NyxIdChatBrowserActions.IsRequestExecutable(request) ||
            !NyxIdChatBrowserActions.RequestParamsMatchAction(request) ||
            (requireRecoverableAction && !IsRecoverableAction(request.Action)) ||
            string.IsNullOrEmpty(WireAction(request.Action)) ||
            request.Params is null ||
            request.RequestedAt is null ||
            !request.HasSourceToolStepId ||
            !IsNormalizedRequired(request.ConversationActorId) ||
            !IsNormalizedRequired(request.OriginTurnId) ||
            !IsNormalizedRequired(request.TaskId) ||
            !IsNormalizedRequired(request.StepId) ||
            !IsNormalizedRequired(request.ActionRequestId) ||
            !IsNormalizedRequired(request.SourceToolStepId) ||
            request.AdvisoryRisk != NyxIdAssistantActionRisk.Grant ||
            request.RememberEligible !=
            (request.Action == NyxIdAssistantActionKind.ServiceConnect) ||
            !string.Equals(request.ConversationActorId, state.ConversationActorId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var expectedRequestId = BuildStableIdentity(
            "action",
            state.ConversationActorId,
            request.OriginTurnId,
            request.TaskId,
            request.SourceToolStepId,
            WireAction(request.Action),
            request.Params.ToByteString().ToBase64());
        var expectedStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            request.TaskId,
            expectedRequestId,
            "browser-action");
        return string.Equals(request.ActionRequestId, expectedRequestId,
                   StringComparison.Ordinal) &&
               string.Equals(request.StepId, expectedStepId, StringComparison.Ordinal);
    }

    private static bool HasValidCompletedSourceTool(
        NyxIdChatTaskState task,
        NyxIdChatActionRequestState request)
    {
        if (!request.HasSourceToolStepId)
            return false;

        var sourceSteps = task.Steps
            .Where(candidate => string.Equals(
                candidate.StepId,
                request.SourceToolStepId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (sourceSteps.Length != 1)
            return false;

        var source = sourceSteps[0];
        return source is
               {
                   Kind: NyxIdChatStepKind.Tool,
                   Status: NyxIdChatStepStatus.Done,
                   Required: true,
                   ExternalEffect: NyxIdChatEffectEvidence.NotApplied,
                   Operation:
                   {
                       Kind: NyxIdChatStepKind.Tool,
                       Phase: NyxIdChatOperationPhase.Succeeded,
                       Key: not null,
                       CompletedAt: not null,
                   },
               } &&
               string.IsNullOrEmpty(source.FailureCode) &&
               string.IsNullOrEmpty(source.Operation.TerminalCode) &&
               string.Equals(source.Operation.Key.ConversationActorId,
                   request.ConversationActorId, StringComparison.Ordinal) &&
               string.Equals(source.Operation.Key.TurnId,
                   request.OriginTurnId, StringComparison.Ordinal) &&
               string.Equals(source.Operation.Key.TaskId,
                   request.TaskId, StringComparison.Ordinal) &&
               string.Equals(source.Operation.Key.StepId,
                   request.SourceToolStepId, StringComparison.Ordinal) &&
               source.Operation.Key.OperationGeneration > 0;
    }

    private static NyxIdChatConversationGAgentState UpgradeBinding(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        NyxIdAssistantActionKind action,
        Timestamp now)
    {
        var upgraded = state.Clone();
        var step = FindExactStep(upgraded, key)!;
        step.Source.Postcondition.Action = action;
        step.Source.Postcondition.VerificationInputBinding =
            NyxIdChatVerificationInputBinding.Sha256V1;
        step.UpdatedAt = now.Clone();
        upgraded.ActiveTask.UpdatedAt = now.Clone();
        upgraded.ProgressSequence = checked(state.ProgressSequence + 1);
        upgraded.UpdatedAt = now.Clone();
        return upgraded;
    }

    private static RecoveryContext RequireInFlightContext(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key) =>
        InspectInFlight(state, key, requireRequestedPhase: true).Context!;

    private static NyxIdChatOperationDispatchCommand BuildCommand(
        NyxIdChatConversationGAgentState state,
        RecoveryContext context,
        NyxIdChatOperationKey key) =>
        NyxIdChatBrowserActions.BuildPostconditionCommand(
            state.ScopeId,
            context.Admission.OwnerSubject,
            context.Request,
            context.Report,
            key);

    private static NyxIdChatTaskStepState? FindExactStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key)
    {
        var matches = state.ActiveTask?.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, key))
            .Take(2)
            .ToArray() ?? [];
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool HasBrowserHint(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key) =>
        state.ActiveTask?.Steps.Any(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition &&
            (string.Equals(candidate.StepId, key.StepId, StringComparison.Ordinal) ||
             string.Equals(candidate.Operation?.Key?.OperationId, key.OperationId,
                 StringComparison.Ordinal) ||
             string.Equals(candidate.StepId, state.ActiveTask.ActiveStepId,
                 StringComparison.Ordinal)) &&
            (IsRecoverableAction(candidate.Source?.Postcondition?.Action ??
                                 NyxIdAssistantActionKind.Unspecified) ||
             state.PendingActions.Any(request =>
                 IsRecoverableAction(request.Action) &&
                 (string.Equals(request.ActionRequestId,
                      candidate.ActionRequestId, StringComparison.Ordinal) ||
                  string.Equals(request.ActionRequestId,
                      candidate.Source?.Postcondition?.ActionRequestId,
                      StringComparison.Ordinal))))) == true;

    private static NyxIdChatOperationKey BuildOperationKey(
        string actorId,
        string turnId,
        string taskId,
        string stepId,
        long generation) =>
        new()
        {
            ConversationActorId = actorId,
            TurnId = turnId,
            TaskId = taskId,
            StepId = stepId,
            OperationId = BuildStableIdentity(
                "operation",
                actorId,
                turnId,
                taskId,
                stepId,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            OperationGeneration = generation,
        };

    private static bool IsRecoverableAction(NyxIdAssistantActionKind action) =>
        action is NyxIdAssistantActionKind.ServiceConnect or
            NyxIdAssistantActionKind.KeyCreate or
            NyxIdAssistantActionKind.KeyRotate;

    private static string WireAction(NyxIdAssistantActionKind action) =>
        action switch
        {
            NyxIdAssistantActionKind.ServiceConnect => "service.connect",
            NyxIdAssistantActionKind.ServiceAccessReview => "service.access_review",
            NyxIdAssistantActionKind.KeyCreate => "key.create",
            NyxIdAssistantActionKind.KeyRotate => "key.rotate",
            NyxIdAssistantActionKind.ServiceReauthorize => "service.reauthorize",
            _ => string.Empty,
        };

    private static bool IsNormalizedRequired(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId,
            StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static NyxIdChatBrowserActionPostconditionRecoveryDecision Decision(
        NyxIdChatBrowserActionPostconditionRecoveryStatus status,
        NyxIdChatConversationGAgentState state) =>
        new(
            status,
            state.Clone(),
            null,
            null,
            NyxIdAssistantActionKind.Unspecified);

    private sealed record RecoveryContext(
        NyxIdChatTaskStepState PostconditionStep,
        NyxIdChatActionRequestState Request,
        NyxIdChatContinuationAdmissionState Admission,
        NyxIdChatActionReport? Report,
        bool LegacyContract);

    private sealed record Inspection(
        NyxIdChatBrowserActionPostconditionRecoveryStatus Status,
        RecoveryContext? Context)
    {
        internal static Inspection NotApplicable { get; } =
            new(NyxIdChatBrowserActionPostconditionRecoveryStatus.NotApplicable, null);

        internal static Inspection Invalid { get; } =
            new(NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid, null);
    }
}
