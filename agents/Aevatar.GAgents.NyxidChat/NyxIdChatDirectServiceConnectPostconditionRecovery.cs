using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal enum NyxIdChatDirectServiceConnectRecoveryStatus
{
    NotApplicable,
    Ready,
    UpgradeRequired,
    CompletedUpgradeRequired,
    Invalid,
}

internal sealed record NyxIdChatDirectServiceConnectRecoveryDecision(
    NyxIdChatDirectServiceConnectRecoveryStatus Status,
    NyxIdChatConversationGAgentState State,
    NyxIdChatOperationDispatchCommand? Command,
    NyxIdChatOperationKey? OriginalKey,
    bool LegacyContract,
    string ReasonCode,
    string SafeMessage);

/// <summary>
/// Rebuilds the credential-free direct service-connect postcondition solely
/// from actor-owned facts. This is intentionally narrower than browser-action
/// recovery and refuses every state that cannot be proven to have been emitted
/// by the historical direct service.connect lifecycle.
/// </summary>
internal static class NyxIdChatDirectServiceConnectPostconditionRecovery
{
    internal const string UpgradeFailedCode =
        NyxIdChatPostconditionContractUpgradeFailure.Code;
    internal const string UpgradeFailedMessage =
        NyxIdChatPostconditionContractUpgradeFailure.SafeMessage;

    private const string RequireServiceToolName = "nyxid_require_service";
    private const string ServiceConnectedCheck = "service.connected";

    internal static NyxIdChatDirectServiceConnectRecoveryDecision BuildRequestedRecovery(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectInFlight(state, key, requireRequestedPhase: true);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state, null, false);
        }

        var context = inspection.Context;
        if (!context.LegacyContract)
        {
            return new NyxIdChatDirectServiceConnectRecoveryDecision(
                NyxIdChatDirectServiceConnectRecoveryStatus.Ready,
                state.Clone(),
                BuildCommand(state, context.PostconditionStep, context.EffectStep),
                key.Clone(),
                LegacyContract: false,
                string.Empty,
                string.Empty);
        }

        var upgraded = UpgradeBinding(state, key, now);
        var upgradedStep = FindExactStep(upgraded, key)!;
        var upgradedEffect = FindEffectStep(upgraded, upgradedStep)!;
        return new NyxIdChatDirectServiceConnectRecoveryDecision(
            NyxIdChatDirectServiceConnectRecoveryStatus.UpgradeRequired,
            upgraded,
            BuildCommand(upgraded, upgradedStep, upgradedEffect),
            key.Clone(),
            LegacyContract: true,
            string.Empty,
            string.Empty);
    }

    internal static NyxIdChatDirectServiceConnectRecoveryDecision BuildInFlightUpgrade(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectInFlight(state, key, requireRequestedPhase: false);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state, null, false);
        }

        if (!inspection.Context.LegacyContract)
        {
            return new NyxIdChatDirectServiceConnectRecoveryDecision(
                NyxIdChatDirectServiceConnectRecoveryStatus.Ready,
                state.Clone(),
                null,
                key.Clone(),
                LegacyContract: false,
                string.Empty,
                string.Empty);
        }

        return new NyxIdChatDirectServiceConnectRecoveryDecision(
            NyxIdChatDirectServiceConnectRecoveryStatus.UpgradeRequired,
            UpgradeBinding(state, key, now),
            null,
            key.Clone(),
            LegacyContract: true,
            string.Empty,
            string.Empty);
    }

    internal static NyxIdChatDirectServiceConnectRecoveryDecision BuildCompletedUpgrade(
        NyxIdChatConversationGAgentState state,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(now);

        var inspection = InspectCompleted(state);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state, null, null, false);
        }

        var context = inspection.Context;
        if (!context.LegacyContract)
        {
            return new NyxIdChatDirectServiceConnectRecoveryDecision(
                NyxIdChatDirectServiceConnectRecoveryStatus.Ready,
                state.Clone(),
                null,
                context.PostconditionStep.Operation.Key.Clone(),
                LegacyContract: false,
                string.Empty,
                string.Empty);
        }

        var key = context.PostconditionStep.Operation.Key;
        var upgraded = UpgradeBinding(state, key, now);
        return new NyxIdChatDirectServiceConnectRecoveryDecision(
            NyxIdChatDirectServiceConnectRecoveryStatus.CompletedUpgradeRequired,
            upgraded,
            null,
            key.Clone(),
            LegacyContract: true,
            string.Empty,
            string.Empty);
    }

    internal static NyxIdChatDirectServiceConnectRecoveryDecision BuildFreshRedispatch(
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
                NyxIdChatDirectServiceConnectRecoveryStatus.NotApplicable,
                state,
                null,
                false);
        }

        var inspection = InspectInFlight(state, legacyResult.Key, requireRequestedPhase: false);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null)
        {
            return Decision(inspection.Status, state, null, false);
        }

        var context = inspection.Context;
        if (legacyResult.Key.OperationGeneration != 1)
        {
            return Decision(
                NyxIdChatDirectServiceConnectRecoveryStatus.Invalid,
                state,
                null,
                legacyResult.Key,
                context.LegacyContract);
        }

        if (!string.Equals(
                legacyResult.ActionPostcondition.ActionRequestId,
                context.PostconditionStep.Source.Postcondition.ActionRequestId,
                StringComparison.Ordinal))
        {
            return Decision(
                NyxIdChatDirectServiceConnectRecoveryStatus.Invalid,
                state,
                null,
                legacyResult.Key,
                context.LegacyContract);
        }

        var upgraded = state.Clone();
        if (KeysEqual(upgraded.PendingOperationDeliveryProbe, legacyResult.Key))
            upgraded.PendingOperationDeliveryProbe = null;
        var upgradedStep = FindExactStep(upgraded, legacyResult.Key)!;
        upgradedStep.Source.Postcondition.Action = NyxIdAssistantActionKind.ServiceConnect;
        upgradedStep.Source.Postcondition.VerificationInputBinding =
            NyxIdChatVerificationInputBinding.Sha256V1;
        var generation = checked(legacyResult.Key.OperationGeneration + 1);
        var redispatchKey = new NyxIdChatOperationKey
        {
            ConversationActorId = upgraded.ConversationActorId,
            TurnId = legacyResult.Key.TurnId,
            TaskId = legacyResult.Key.TaskId,
            StepId = upgradedStep.StepId,
            OperationId = BuildStableIdentity(
                "operation",
                upgraded.ConversationActorId,
                legacyResult.Key.TurnId,
                legacyResult.Key.TaskId,
                upgradedStep.StepId,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            OperationGeneration = generation,
        };
        upgradedStep.Status = NyxIdChatStepStatus.Running;
        upgradedStep.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        upgradedStep.FailureCode = string.Empty;
        upgradedStep.SafeMessage = string.Empty;
        upgradedStep.Operation = new NyxIdChatOperationState
        {
            Key = redispatchKey.Clone(),
            Kind = NyxIdChatStepKind.Postcondition,
            Phase = NyxIdChatOperationPhase.Requested,
            RequestedAt = now.Clone(),
        };
        upgradedStep.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(upgradedStep);
        upgradedStep.UpdatedAt = now.Clone();
        upgraded.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        upgraded.ActiveTask.ActiveStepId = upgradedStep.StepId;
        upgraded.ActiveTask.ActiveOperationId = redispatchKey.OperationId;
        upgraded.ActiveTask.FailureCode = string.Empty;
        upgraded.ActiveTask.SafeMessage = string.Empty;
        upgraded.ActiveTask.UpdatedAt = now.Clone();
        upgraded.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        upgraded.ActiveTurn.FailureCode = string.Empty;
        upgraded.ActiveTurn.SafeMessage = string.Empty;
        upgraded.ActiveTurn.TerminalAt = null;
        upgraded.LatestTurn = upgraded.ActiveTurn.Clone();
        upgraded.ProgressSequence = checked(state.ProgressSequence + 1);
        upgraded.UpdatedAt = now.Clone();
        var upgradedEffect = FindEffectStep(upgraded, upgradedStep)!;
        return new NyxIdChatDirectServiceConnectRecoveryDecision(
            NyxIdChatDirectServiceConnectRecoveryStatus.UpgradeRequired,
            upgraded,
            BuildCommand(upgraded, upgradedStep, upgradedEffect),
            legacyResult.Key.Clone(),
            context.LegacyContract,
            string.Empty,
            string.Empty);
    }

    internal static bool MatchesExpectedInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        NyxIdChatActionPostconditionResult result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(result);

        if (step.Operation?.Key is null)
            return false;
        var inspection = InspectInFlight(
            state,
            step.Operation.Key,
            requireRequestedPhase: false);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null ||
            inspection.Context.LegacyContract)
        {
            return false;
        }

        var expected = BuildCommand(
            state,
            inspection.Context.PostconditionStep,
            inspection.Context.EffectStep).ActionPostcondition;
        return NyxIdChatActionPostconditionEvidence.Matches(expected, result);
    }

    internal static bool HasVerifiedCompletedDirectState(
        NyxIdChatConversationGAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var inspection = InspectCompleted(state);
        return inspection is
        {
            Status: NyxIdChatDirectServiceConnectRecoveryStatus.Ready,
            Context.LegacyContract: false,
        };
    }

    internal static bool HasVerifiedCompletedDirectState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        var inspection = InspectStableDirectStep(state, key);
        if (inspection.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            inspection.Context is null ||
            inspection.Context.LegacyContract)
        {
            return false;
        }

        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        var step = inspection.Context.PostconditionStep;
        return task is not null &&
               turn is not null &&
               state.PendingActions.Count == 0 &&
               state.ContinuationAdmission is null &&
               string.Equals(task.TaskId, key.TaskId, StringComparison.Ordinal) &&
               string.Equals(task.TurnId, key.TurnId, StringComparison.Ordinal) &&
               string.Equals(turn.TaskId, key.TaskId, StringComparison.Ordinal) &&
               string.Equals(turn.TurnId, key.TurnId, StringComparison.Ordinal) &&
               step.Status == NyxIdChatStepStatus.Done &&
               step.Required &&
               step.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
               string.IsNullOrEmpty(step.FailureCode) &&
               step.Operation is
               {
                   Kind: NyxIdChatStepKind.Postcondition,
                   Phase: NyxIdChatOperationPhase.Succeeded,
                   RequestedAt: not null,
                   CompletedAt: not null,
               } &&
               string.IsNullOrEmpty(step.Operation.TerminalCode);
    }

    private static Inspection InspectInFlight(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        bool requireRequestedPhase)
    {
        var stable = InspectStableDirectStep(state, key);
        if (stable.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            stable.Context is null)
        {
            return stable;
        }

        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        var step = stable.Context.PostconditionStep;
        var phaseValid = requireRequestedPhase
            ? step.Operation?.Phase == NyxIdChatOperationPhase.Requested
            : step.Operation?.Phase is NyxIdChatOperationPhase.Requested or
                NyxIdChatOperationPhase.Dispatched or
                NyxIdChatOperationPhase.Running;
        var valid = state.PendingActions.Count == 0 &&
                    state.ContinuationAdmission is null &&
                    state.ControlFence is null &&
                    task.Status == NyxIdChatTaskStatus.Active &&
                    turn.Status == NyxIdChatTurnStatus.Active &&
                    step.Status == NyxIdChatStepStatus.Running &&
                    step.Required &&
                    step.ExternalEffect == NyxIdChatEffectEvidence.NotStarted &&
                    step.Operation is not null &&
                    step.Operation.Kind == NyxIdChatStepKind.Postcondition &&
                    step.Operation.RequestedAt is not null &&
                    step.Operation.CompletedAt is null &&
                    phaseValid &&
                    string.Equals(task.ActiveStepId, step.StepId, StringComparison.Ordinal) &&
                    string.Equals(task.ActiveOperationId, key.OperationId, StringComparison.Ordinal) &&
                    string.Equals(task.TaskId, key.TaskId, StringComparison.Ordinal) &&
                    string.Equals(task.TurnId, key.TurnId, StringComparison.Ordinal) &&
                    string.Equals(turn.TaskId, key.TaskId, StringComparison.Ordinal) &&
                    string.Equals(turn.TurnId, key.TurnId, StringComparison.Ordinal);

        return valid ? stable : Inspection.Invalid;
    }

    private static Inspection InspectCompleted(NyxIdChatConversationGAgentState state)
    {
        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        if (task is null ||
            turn is null ||
            turn.Intent != NyxIdChatTurnIntent.ServiceConnect)
        {
            return Inspection.NotApplicable;
        }

        var directHints = task.Steps.Where(IsDirectPostconditionHint).ToArray();
        if (directHints.Length == 0)
            return Inspection.NotApplicable;
        if (directHints.Length != 1 || directHints[0].Operation?.Key is null)
            return Inspection.Invalid;

        var stable = InspectStableDirectStep(state, directHints[0].Operation.Key);
        if (stable.Status != NyxIdChatDirectServiceConnectRecoveryStatus.Ready ||
            stable.Context is null)
        {
            return Inspection.Invalid;
        }

        var step = stable.Context.PostconditionStep;
        var activeSteps = task.Steps.Where(candidate => string.Equals(
            candidate.StepId,
            task.ActiveStepId,
            StringComparison.Ordinal)).ToArray();
        var activeStep = activeSteps.Length == 1 ? activeSteps[0] : null;
        var activeKey = activeStep?.Operation?.Key;
        var expectedActiveOperationId = activeKey is null ||
                                        activeKey.OperationGeneration <= 0
            ? string.Empty
            : BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                activeKey.TurnId,
                activeKey.TaskId,
                activeStep!.StepId,
                activeKey.OperationGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        var valid = state.PendingActions.Count == 0 &&
                    state.ContinuationAdmission is null &&
                    state.ControlFence is null &&
                    task.Status == NyxIdChatTaskStatus.Active &&
                    turn.Status == NyxIdChatTurnStatus.Active &&
                    string.Equals(task.TaskId, step.Operation.Key.TaskId, StringComparison.Ordinal) &&
                    string.Equals(turn.TaskId, task.TaskId, StringComparison.Ordinal) &&
                    string.Equals(task.TurnId, turn.TurnId, StringComparison.Ordinal) &&
                    step.Status == NyxIdChatStepStatus.Done &&
                    step.Required &&
                    step.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
                    step.Operation.Kind == NyxIdChatStepKind.Postcondition &&
                    step.Operation.Phase == NyxIdChatOperationPhase.Succeeded &&
                    step.Operation.RequestedAt is not null &&
                    step.Operation.CompletedAt is not null &&
                    string.IsNullOrEmpty(step.FailureCode) &&
                    string.IsNullOrEmpty(step.Operation.TerminalCode) &&
                    activeStep is
                    {
                        Kind: NyxIdChatStepKind.Llm,
                        Status: NyxIdChatStepStatus.Running,
                        Operation.Kind: NyxIdChatStepKind.Llm,
                    } &&
                    activeStep.Source?.Llm is not null &&
                    activeKey is not null &&
                    activeStep.Operation.RequestedAt is not null &&
                    activeStep.Operation.CompletedAt is null &&
                    activeStep.Operation.Phase is NyxIdChatOperationPhase.Requested or
                        NyxIdChatOperationPhase.Dispatched or
                        NyxIdChatOperationPhase.Running &&
                    activeStep.DependsOn.Contains(step.StepId, StringComparer.Ordinal) &&
                    task.Steps.Count(candidate => string.Equals(
                        candidate.StepId,
                        activeStep.StepId,
                        StringComparison.Ordinal)) == 1 &&
                    string.Equals(
                        activeKey.ConversationActorId,
                        state.ConversationActorId,
                        StringComparison.Ordinal) &&
                    string.Equals(activeKey.TurnId, turn.TurnId,
                        StringComparison.Ordinal) &&
                    string.Equals(activeKey.TurnId, task.TurnId,
                        StringComparison.Ordinal) &&
                    string.Equals(activeKey.TaskId, task.TaskId,
                        StringComparison.Ordinal) &&
                    string.Equals(activeKey.StepId, activeStep.StepId,
                        StringComparison.Ordinal) &&
                    activeKey.OperationGeneration > 0 &&
                    string.Equals(activeKey.OperationId, expectedActiveOperationId,
                        StringComparison.Ordinal) &&
                    string.Equals(task.ActiveOperationId, activeKey.OperationId,
                        StringComparison.Ordinal);
        return valid ? stable : Inspection.Invalid;
    }

    private static Inspection InspectStableDirectStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key)
    {
        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        if (task is null ||
            turn is null ||
            turn.Intent != NyxIdChatTurnIntent.ServiceConnect)
        {
            return Inspection.NotApplicable;
        }

        var matchingSteps = task.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, key))
            .ToArray();
        var directHints = task.Steps
            .Where(candidate =>
                IsDirectPostconditionHint(candidate) &&
                (string.Equals(candidate.StepId, key.StepId, StringComparison.Ordinal) ||
                 string.Equals(candidate.Operation?.Key?.OperationId, key.OperationId,
                     StringComparison.Ordinal) ||
                 string.Equals(candidate.StepId, task.ActiveStepId, StringComparison.Ordinal)))
            .ToArray();
        if (matchingSteps.Length != 1)
        {
            return directHints.Length == 0
                ? Inspection.NotApplicable
                : Inspection.Invalid;
        }

        var step = matchingSteps[0];
        var source = step.Source?.Postcondition;
        if (step.Kind != NyxIdChatStepKind.Postcondition || source is null)
            return Inspection.NotApplicable;

        var exactCheck = string.Equals(source.Check, ServiceConnectedCheck, StringComparison.Ordinal);
        var pendingBrowserAction = state.PendingActions.Any(action => string.Equals(
            action.ActionRequestId,
            source.ActionRequestId,
            StringComparison.Ordinal));
        if (!exactCheck && pendingBrowserAction)
            return Inspection.NotApplicable;
        if (!exactCheck && source.Action != NyxIdAssistantActionKind.ServiceConnect)
            return Inspection.NotApplicable;

        var providerResourceId = source.ProviderResourceId;
        var effectSteps = task.Steps
            .Where(candidate => string.Equals(
                candidate.StepId,
                source.EffectStepId,
                StringComparison.Ordinal))
            .ToArray();
        var effectStep = effectSteps.Length == 1 ? effectSteps[0] : null;
        var effectSource = effectStep?.Source?.Tool;
        var effectOperation = effectStep?.Operation;
        var effectKey = effectOperation?.Key;
        var serviceConnect = effectSource?.ServiceConnectPostcondition;
        var readiness = effectSource?.AuthorizationReadiness;
        var expectedActionRequestId = string.IsNullOrWhiteSpace(providerResourceId)
            ? string.Empty
            : BuildStableIdentity(
                "action-postcondition",
                state.ConversationActorId,
                key.TurnId,
                key.TaskId,
                source.EffectStepId,
                providerResourceId);
        var expectedStepId = string.IsNullOrWhiteSpace(providerResourceId)
            ? string.Empty
            : BuildStableIdentity(
                "step",
                state.ConversationActorId,
                key.TurnId,
                key.TaskId,
                source.EffectStepId,
                providerResourceId,
                "service-connected-postcondition");
        var expectedOperationId = string.IsNullOrWhiteSpace(expectedStepId) ||
                                  key.OperationGeneration <= 0
            ? string.Empty
            : BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                key.TurnId,
                key.TaskId,
                expectedStepId,
                key.OperationGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        var expectedEffectOperationId = effectKey is null || effectKey.OperationGeneration <= 0
            ? string.Empty
            : BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                key.TurnId,
                key.TaskId,
                source.EffectStepId,
                effectKey.OperationGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        var legacyContract =
            (source.Action is NyxIdAssistantActionKind.Unspecified or
                NyxIdAssistantActionKind.ServiceConnect) &&
            source.VerificationInputBinding ==
            NyxIdChatVerificationInputBinding.Unspecified;
        var actionValid = legacyContract ||
                          (source.Action == NyxIdAssistantActionKind.ServiceConnect &&
                           source.VerificationInputBinding ==
                           NyxIdChatVerificationInputBinding.Sha256V1);
        var scopesValid = serviceConnect is not null &&
                          serviceConnect.RequestedScopes.Count <= 64 &&
                          serviceConnect.RequestedScopes.All(scope =>
                              IsNormalizedBounded(scope, 256)) &&
                          serviceConnect.RequestedScopes.Distinct(StringComparer.Ordinal).Count() ==
                          serviceConnect.RequestedScopes.Count;
        var readinessMatches = serviceConnect is not null &&
                               readiness?.Params is not null &&
                               string.Equals(readiness.ToolName, RequireServiceToolName,
                                   StringComparison.Ordinal) &&
                               string.Equals(readiness.Params.ServiceSlug,
                                   serviceConnect.ServiceSlug, StringComparison.Ordinal) &&
                               readiness.Params.RequestedScopes.SequenceEqual(
                                   serviceConnect.RequestedScopes,
                                   StringComparer.Ordinal);
        var providerSourceCount = task.Steps.Count(candidate =>
            candidate.Kind == NyxIdChatStepKind.Tool &&
            string.Equals(candidate.Source?.Tool?.ToolName, RequireServiceToolName,
                StringComparison.Ordinal) &&
            string.Equals(candidate.Source?.Tool?.ProviderResourceId, providerResourceId,
                StringComparison.Ordinal) &&
            candidate.Source?.Tool?.ServiceConnectPostcondition is not null);
        var valid = exactCheck &&
                    !pendingBrowserAction &&
                    actionValid &&
                    (!legacyContract || key.OperationGeneration == 1) &&
                    source.ToolReadBack is null &&
                    string.IsNullOrEmpty(step.ActionRequestId) &&
                    IsNormalizedRequired(state.ConversationActorId) &&
                    IsNormalizedRequired(state.ScopeId) &&
                    IsNormalizedRequired(state.OwnerSubject) &&
                    IsNormalizedRequired(providerResourceId) &&
                    serviceConnect is not null &&
                    IsNormalizedBounded(serviceConnect.ServiceSlug, 128) &&
                    IsNormalizedOptional(serviceConnect.ViaNodeId) &&
                    IsNormalizedOptional(serviceConnect.TargetOrgId) &&
                    scopesValid &&
                    readinessMatches &&
                    effectStep is
                    {
                        Kind: NyxIdChatStepKind.Tool,
                        Status: NyxIdChatStepStatus.Done,
                        Required: true,
                        MayChangeExternalState: false,
                        ExternalEffect: NyxIdChatEffectEvidence.NotApplied,
                        Operation.Kind: NyxIdChatStepKind.Tool,
                        Operation.Phase: NyxIdChatOperationPhase.Succeeded,
                        Operation.MayChangeExternalState: false,
                        Operation.Idempotent: true,
                    } &&
                    string.IsNullOrEmpty(effectStep.FailureCode) &&
                    string.IsNullOrEmpty(effectOperation!.TerminalCode) &&
                    effectOperation.RequestedAt is not null &&
                    effectOperation.CompletedAt is not null &&
                    effectKey is not null &&
                    effectKey.OperationGeneration == 1 &&
                    string.Equals(effectOperation.IdempotencyKey, expectedEffectOperationId,
                        StringComparison.Ordinal) &&
                    string.Equals(effectSource?.ToolName, RequireServiceToolName,
                        StringComparison.Ordinal) &&
                    string.Equals(effectSource!.ProviderResourceId, providerResourceId,
                        StringComparison.Ordinal) &&
                    providerSourceCount == 1 &&
                    string.Equals(effectKey.ConversationActorId, state.ConversationActorId,
                        StringComparison.Ordinal) &&
                    string.Equals(effectKey.TurnId, key.TurnId, StringComparison.Ordinal) &&
                    string.Equals(effectKey.TaskId, key.TaskId, StringComparison.Ordinal) &&
                    string.Equals(effectKey.StepId, effectStep.StepId,
                        StringComparison.Ordinal) &&
                    string.Equals(effectKey.OperationId, expectedEffectOperationId,
                        StringComparison.Ordinal) &&
                    step.DependsOn.Count == 1 &&
                    string.Equals(step.DependsOn[0], effectStep.StepId, StringComparison.Ordinal) &&
                    task.Steps.Count(candidate => string.Equals(
                        candidate.StepId,
                        step.StepId,
                        StringComparison.Ordinal)) == 1 &&
                    string.Equals(task.TaskId, key.TaskId, StringComparison.Ordinal) &&
                    string.Equals(key.ConversationActorId, state.ConversationActorId,
                        StringComparison.Ordinal) &&
                    string.Equals(key.StepId, step.StepId, StringComparison.Ordinal) &&
                    string.Equals(source.ActionRequestId, expectedActionRequestId,
                        StringComparison.Ordinal) &&
                    string.Equals(step.StepId, expectedStepId, StringComparison.Ordinal) &&
                    string.Equals(key.OperationId, expectedOperationId, StringComparison.Ordinal);

        return valid
            ? new Inspection(
                NyxIdChatDirectServiceConnectRecoveryStatus.Ready,
                new RecoveryContext(step, effectStep!, legacyContract))
            : Inspection.Invalid;
    }

    private static NyxIdChatConversationGAgentState UpgradeBinding(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        var upgraded = state.Clone();
        var upgradedStep = FindExactStep(upgraded, key)!;
        upgradedStep.Source.Postcondition.Action = NyxIdAssistantActionKind.ServiceConnect;
        upgradedStep.Source.Postcondition.VerificationInputBinding =
            NyxIdChatVerificationInputBinding.Sha256V1;
        upgradedStep.UpdatedAt = now.Clone();
        upgraded.ActiveTask.UpdatedAt = now.Clone();
        upgraded.ProgressSequence = checked(state.ProgressSequence + 1);
        upgraded.UpdatedAt = now.Clone();
        return upgraded;
    }

    private static NyxIdChatOperationDispatchCommand BuildCommand(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState postconditionStep,
        NyxIdChatTaskStepState effectStep)
    {
        var source = postconditionStep.Source.Postcondition;
        return new NyxIdChatOperationDispatchCommand
        {
            Key = postconditionStep.Operation.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionInput
            {
                ScopeId = state.ScopeId,
                OwnerSubject = state.OwnerSubject,
                OriginTurnId = postconditionStep.Operation.Key.TurnId,
                ActionRequestId = source.ActionRequestId,
                Action = NyxIdAssistantActionKind.ServiceConnect,
                ReportedDisposition = NyxIdChatActionDisposition.Completed,
                ResourceHint = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = source.ProviderResourceId,
                    },
                },
                Params = new NyxIdAssistantActionParams
                {
                    CatalogServiceConnect =
                        effectStep.Source.Tool.ServiceConnectPostcondition.Clone(),
                },
                RequestedAt = postconditionStep.Operation.RequestedAt.Clone(),
            },
        };
    }

    private static NyxIdChatTaskStepState? FindExactStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key) =>
        state.ActiveTask?.Steps.SingleOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, key));

    private static NyxIdChatTaskStepState? FindEffectStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState postconditionStep) =>
        state.ActiveTask?.Steps.SingleOrDefault(candidate => string.Equals(
            candidate.StepId,
            postconditionStep.Source?.Postcondition?.EffectStepId,
            StringComparison.Ordinal));

    private static bool IsDirectPostconditionHint(NyxIdChatTaskStepState step) =>
        step.Kind == NyxIdChatStepKind.Postcondition &&
        string.Equals(
            step.Source?.Postcondition?.Check,
            ServiceConnectedCheck,
            StringComparison.Ordinal);

    private static bool IsNormalizedRequired(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsNormalizedBounded(string? value, int maximumLength) =>
        IsNormalizedRequired(value) &&
        value!.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsNormalizedOptional(string? value) =>
        string.IsNullOrEmpty(value) ||
        (string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
         !value.Any(char.IsControl));

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
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static NyxIdChatDirectServiceConnectRecoveryDecision Decision(
        NyxIdChatDirectServiceConnectRecoveryStatus status,
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationDispatchCommand? command,
        bool legacyContract) =>
        Decision(status, state, command, null, legacyContract);

    private static NyxIdChatDirectServiceConnectRecoveryDecision Decision(
        NyxIdChatDirectServiceConnectRecoveryStatus status,
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationDispatchCommand? command,
        NyxIdChatOperationKey? originalKey,
        bool legacyContract) =>
        new(
            status,
            state.Clone(),
            command,
            originalKey?.Clone(),
            legacyContract,
            status == NyxIdChatDirectServiceConnectRecoveryStatus.Invalid
                ? UpgradeFailedCode
                : string.Empty,
            status == NyxIdChatDirectServiceConnectRecoveryStatus.Invalid
                ? UpgradeFailedMessage
                : string.Empty);

    private sealed record RecoveryContext(
        NyxIdChatTaskStepState PostconditionStep,
        NyxIdChatTaskStepState EffectStep,
        bool LegacyContract);

    private sealed record Inspection(
        NyxIdChatDirectServiceConnectRecoveryStatus Status,
        RecoveryContext? Context)
    {
        internal static Inspection NotApplicable { get; } =
            new(NyxIdChatDirectServiceConnectRecoveryStatus.NotApplicable, null);

        internal static Inspection Invalid { get; } =
            new(NyxIdChatDirectServiceConnectRecoveryStatus.Invalid, null);
    }
}
