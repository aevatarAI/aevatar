using Aevatar.AI.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatCanaryEffectFaultDecisions
{
    internal static readonly TimeSpan MaximumArmLifetime = TimeSpan.FromMinutes(15);

    public static bool TryArm(
        NyxIdChatConversationGAgentState state,
        NyxIdChatCanaryEffectFaultArmCommand command,
        long stateVersion,
        Timestamp now,
        out NyxIdChatConversationGAgentState next)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);
        next = state;

        if (IsExactReplay(state.CanaryEffectFault?.ArmIntent, command))
            return false;
        if (!IsValidArm(state, command, stateVersion, now) ||
            state.CanaryEffectFault is
            {
                Status: NyxIdChatCanaryEffectFaultStatus.Armed,
                ArmIntent.ExpiresAt: { } existingExpiry,
            } && existingExpiry.ToDateTimeOffset() > now.ToDateTimeOffset())
        {
            return false;
        }

        next = state.Clone();
        next.CanaryEffectFault = new NyxIdChatCanaryEffectFaultState
        {
            ArmIntent = new NyxIdChatCanaryEffectFaultArmIntent
            {
                ArmId = command.ArmId.Trim(),
                ClientRequestId = command.ClientRequestId.Trim(),
                SourceOperationKey = command.SourceOperationKey.Clone(),
                ServiceInstanceId = command.ServiceInstanceId.Trim(),
                OwnerSubject = command.OwnerSubject.Trim(),
                ExpiresAt = command.ExpiresAt.Clone(),
            },
            Status = NyxIdChatCanaryEffectFaultStatus.Armed,
            ArmedAt = now.Clone(),
        };
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return true;
    }

    public static NyxIdChatCanaryEffectFaultDirective? TryAttachToDirectToolDispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey sourceOperationKey,
        NyxIdChatOperationDispatchCommand? dispatch,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sourceOperationKey);
        ArgumentNullException.ThrowIfNull(now);

        var fault = state.CanaryEffectFault;
        var intent = fault?.ArmIntent;
        if (fault?.Status != NyxIdChatCanaryEffectFaultStatus.Armed || intent is null)
            return null;
        if (intent.ExpiresAt?.ToDateTimeOffset() <= now.ToDateTimeOffset())
        {
            fault.Status = NyxIdChatCanaryEffectFaultStatus.Expired;
            return null;
        }

        if (intent.SourceOperationKey is null ||
            !intent.SourceOperationKey.Equals(sourceOperationKey))
        {
            return null;
        }
        if (!string.Equals(state.OwnerSubject, intent.OwnerSubject, StringComparison.Ordinal))
        {
            fault.Status = NyxIdChatCanaryEffectFaultStatus.Expired;
            return null;
        }

        var tool = dispatch?.Tool;
        var operation = tool?.OperationAdmission;
        var safety = EffectSafety(tool?.MayChangeExternalState == true);
        if (dispatch?.Key is null ||
            dispatch.Key.OperationGeneration != 1 ||
            tool is null ||
            !tool.MayChangeExternalState ||
            operation?.ReadBack is null ||
            !string.Equals(operation.ServiceInstanceId, intent.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !IsCatalogDigest(operation.CatalogDigest) ||
            !NyxIdChatOperationAdmissionPolicy.IsValid(operation, safety) ||
            !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(operation.ReadBack, operation))
        {
            fault.Status = NyxIdChatCanaryEffectFaultStatus.Expired;
            return null;
        }

        var directive = new NyxIdChatCanaryEffectFaultDirective
        {
            ArmId = intent.ArmId,
            ClientRequestId = intent.ClientRequestId,
            Key = dispatch.Key.Clone(),
            ServiceInstanceId = intent.ServiceInstanceId,
            CatalogDigest = operation.CatalogDigest,
            OwnerSubject = intent.OwnerSubject,
            ExpiresAt = intent.ExpiresAt.Clone(),
        };
        fault.Directive = directive.Clone();
        fault.Status = NyxIdChatCanaryEffectFaultStatus.Forwarded;
        fault.ForwardedAt = now.Clone();
        tool.CanaryEffectFault = directive.Clone();
        return directive;
    }

    public static bool TryMarkConsumed(
        NyxIdChatConversationGAgentState state,
        NyxIdChatCanaryEffectFaultConsumedSignal signal,
        Timestamp now,
        out NyxIdChatConversationGAgentState next)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);
        next = state;
        var fault = state.CanaryEffectFault;
        var directive = fault?.Directive;
        var exactSteps = state.ActiveTask?.Steps
            .Where(candidate => candidate.Operation?.Key?.Equals(directive?.Key) == true)
            .Take(2)
            .ToArray() ?? [];
        var exactStep = exactSteps.Length == 1 ? exactSteps[0] : null;
        var exactStepIndex = exactStep is null
            ? -1
            : state.ActiveTask!.Steps.IndexOf(exactStep);
        if (fault?.Status != NyxIdChatCanaryEffectFaultStatus.Forwarded ||
            directive?.Key is null ||
            signal.Key is null ||
            signal.ConsumedAt is null ||
            fault.ForwardedAt is null ||
            directive.Key.OperationGeneration != 1 ||
            exactStep?.Source?.Tool?.OperationAdmission is not { } stepAdmission ||
            exactStep.RetryToolInput is not { } retryInput ||
            exactStepIndex < 0 ||
            !string.Equals(directive.ArmId, signal.ArmId, StringComparison.Ordinal) ||
            !directive.Key.Equals(signal.Key) ||
            !string.Equals(signal.ServiceInstanceId, directive.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(stepAdmission.ServiceInstanceId, directive.ServiceInstanceId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(signal.ApprovalRequestId) ||
            signal.ReceiptStatus != AgentToolReceiptStatus.Denied ||
            signal.ApprovalDecisionMode is not (
                NyxIdApprovalDecisionMode.Unspecified or
                NyxIdApprovalDecisionMode.PerRequest) ||
            signal.ApprovalTerminalOutcome != NyxIdApprovalTerminalOutcome.Rejected ||
            !string.Equals(signal.ApprovalSubjectKind, "nyxid.user-service",
                StringComparison.Ordinal) ||
            !string.Equals(signal.ApprovalSubjectId, directive.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(signal.ApprovalCallId, retryInput.CallId,
                StringComparison.Ordinal) ||
            !string.Equals(signal.ApprovalToolName, retryInput.ToolName,
                StringComparison.Ordinal) ||
            !string.Equals(signal.ApprovalToolName, exactStep.Source.Tool.ToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                signal.TurnActorId,
                NyxIdChatTurnActorIds.ForTurn(
                    directive.Key.ConversationActorId,
                    directive.Key.TurnId),
                StringComparison.Ordinal))
        {
            return false;
        }

        next = state.Clone();
        next.CanaryEffectFault.Status = NyxIdChatCanaryEffectFaultStatus.Consumed;
        next.CanaryEffectFault.ConsumedAt = now.Clone();
        next.CanaryEffectFault.ApprovalRequestId = signal.ApprovalRequestId;
        next.CanaryEffectFault.ReceiptStatus = signal.ReceiptStatus;
        next.CanaryEffectFault.ApprovalDecisionMode = signal.ApprovalDecisionMode;
        next.CanaryEffectFault.ApprovalTerminalOutcome = signal.ApprovalTerminalOutcome;
        next.CanaryEffectFault.ApprovalSubjectKind = signal.ApprovalSubjectKind;
        next.CanaryEffectFault.ApprovalSubjectId = signal.ApprovalSubjectId;
        next.CanaryEffectFault.ApprovalCallId = signal.ApprovalCallId;
        next.CanaryEffectFault.ApprovalToolName = signal.ApprovalToolName;
        var nextStep = next.ActiveTask.Steps[exactStepIndex];
        nextStep.ApprovalRequestId = signal.ApprovalRequestId;
        nextStep.ApprovalObservation = new NyxIdChatPostReturnApprovalObservation
        {
            ApprovalRequestId = signal.ApprovalRequestId,
            DecisionMode = signal.ApprovalDecisionMode,
            ReceiptStatus = signal.ReceiptStatus,
            ObservedAt = now.Clone(),
            TerminalOutcome = signal.ApprovalTerminalOutcome,
            SubjectKind = signal.ApprovalSubjectKind,
            SubjectId = signal.ApprovalSubjectId,
        };
        nextStep.UpdatedAt = now.Clone();
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return true;
    }

    internal static bool MatchesTurnDispatch(
        NyxIdChatCanaryEffectFaultDirective? directive,
        NyxIdChatOperationDispatchCommand command,
        AgentToolExecutionContextPayload? toolContext,
        Timestamp now)
    {
        var tool = command.Tool;
        var operation = tool?.OperationAdmission;
        var safety = EffectSafety(tool?.MayChangeExternalState == true);
        return directive?.Key is not null &&
               directive.Key.Equals(command.Key) &&
               directive.Key.OperationGeneration == 1 &&
               directive.ExpiresAt?.ToDateTimeOffset() > now.ToDateTimeOffset() &&
               tool?.MayChangeExternalState == true &&
               operation?.ReadBack is not null &&
               string.Equals(
                   toolContext?.Caller?.OwnerSubject,
                   directive.OwnerSubject,
                   StringComparison.Ordinal) &&
               string.Equals(operation.ServiceInstanceId, directive.ServiceInstanceId,
                   StringComparison.Ordinal) &&
               string.Equals(operation.CatalogDigest, directive.CatalogDigest,
                   StringComparison.Ordinal) &&
               NyxIdChatOperationAdmissionPolicy.IsValid(operation, safety) &&
               NyxIdChatOperationAdmissionPolicy.IsValidReadBack(operation.ReadBack, operation);
    }

    private static bool IsValidArm(
        NyxIdChatConversationGAgentState state,
        NyxIdChatCanaryEffectFaultArmCommand command,
        long stateVersion,
        Timestamp now)
    {
        var expiresAt = command.ExpiresAt?.ToDateTimeOffset();
        var nowValue = now.ToDateTimeOffset();
        return command.ExpectedStateVersion == stateVersion &&
               !string.IsNullOrWhiteSpace(command.ArmId) &&
               !string.IsNullOrWhiteSpace(command.ClientRequestId) &&
               !string.IsNullOrWhiteSpace(command.ScopeId) &&
               !string.IsNullOrWhiteSpace(command.ConversationActorId) &&
               !string.IsNullOrWhiteSpace(command.ServiceInstanceId) &&
               !string.IsNullOrWhiteSpace(command.OwnerSubject) &&
               command.SourceOperationKey is not null &&
               HasCompleteKey(command.SourceOperationKey) &&
               string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
               string.Equals(state.ConversationActorId, command.ConversationActorId.Trim(),
                   StringComparison.Ordinal) &&
               string.Equals(
                   state.ConversationActorId,
                   command.SourceOperationKey.ConversationActorId,
                   StringComparison.Ordinal) &&
               string.Equals(state.OwnerSubject, command.OwnerSubject.Trim(), StringComparison.Ordinal) &&
               expiresAt > nowValue &&
               expiresAt <= nowValue + MaximumArmLifetime &&
               MatchesRunningSourceOperation(state, command.SourceOperationKey);
    }

    private static bool MatchesRunningSourceOperation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey sourceOperationKey)
    {
        var task = state.ActiveTask;
        if (state.ActiveTurn is not { Status: NyxIdChatTurnStatus.Active } activeTurn ||
            task is null ||
            task.Status != NyxIdChatTaskStatus.Active ||
            !string.Equals(activeTurn.TurnId, sourceOperationKey.TurnId, StringComparison.Ordinal) ||
            !string.Equals(activeTurn.TaskId, sourceOperationKey.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.TaskId, sourceOperationKey.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.TurnId, sourceOperationKey.TurnId, StringComparison.Ordinal))
        {
            return false;
        }

        var steps = task.Steps
            .Where(candidate =>
                candidate.Kind == NyxIdChatStepKind.Llm &&
                candidate.Status == NyxIdChatStepStatus.Running &&
                candidate.Operation?.Key?.Equals(sourceOperationKey) == true &&
                candidate.Operation.Phase is
                    NyxIdChatOperationPhase.Requested or
                    NyxIdChatOperationPhase.Dispatched or
                    NyxIdChatOperationPhase.Running)
            .Take(2)
            .ToArray();
        return steps.Length == 1 &&
               string.Equals(task.ActiveStepId, steps[0].StepId, StringComparison.Ordinal) &&
               string.Equals(task.ActiveOperationId, sourceOperationKey.OperationId,
                   StringComparison.Ordinal);
    }

    private static NyxIdChatToolCallSafety EffectSafety(bool mayChangeExternalState) => new()
    {
        IsReadOnly = false,
        IsDestructive = false,
        MayChangeExternalState = mayChangeExternalState,
    };

    private static bool IsExactReplay(
        NyxIdChatCanaryEffectFaultArmIntent? current,
        NyxIdChatCanaryEffectFaultArmCommand command) =>
        current is not null &&
        string.Equals(current.ArmId, command.ArmId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(current.ClientRequestId, command.ClientRequestId?.Trim(),
            StringComparison.Ordinal) &&
        current.SourceOperationKey?.Equals(command.SourceOperationKey) == true &&
        string.Equals(current.ServiceInstanceId, command.ServiceInstanceId?.Trim(),
            StringComparison.Ordinal) &&
        string.Equals(current.OwnerSubject, command.OwnerSubject?.Trim(),
            StringComparison.Ordinal) &&
        current.ExpiresAt?.Equals(command.ExpiresAt) == true;

    private static bool HasCompleteKey(NyxIdChatOperationKey key) =>
        !string.IsNullOrWhiteSpace(key.ConversationActorId) &&
        !string.IsNullOrWhiteSpace(key.TurnId) &&
        !string.IsNullOrWhiteSpace(key.TaskId) &&
        !string.IsNullOrWhiteSpace(key.StepId) &&
        !string.IsNullOrWhiteSpace(key.OperationId) &&
        key.OperationGeneration > 0;

    private static bool IsCatalogDigest(string? value)
    {
        const string prefix = "sha256:";
        return value is not null &&
               value.Length == prefix.Length + 64 &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }
}
