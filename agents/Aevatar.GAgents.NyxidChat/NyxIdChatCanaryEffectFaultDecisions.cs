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

        if (IsExactReplay(state.CanaryEffectFault, command))
            return false;
        if (!IsValidArm(state, command, stateVersion, now, out var catalogDigest) ||
            state.CanaryEffectFault is
            {
                Status: NyxIdChatCanaryEffectFaultStatus.Armed,
            } existing && existing.Directive?.ExpiresAt?.ToDateTimeOffset() > now.ToDateTimeOffset())
        {
            return false;
        }

        next = state.Clone();
        next.CanaryEffectFault = new NyxIdChatCanaryEffectFaultState
        {
            Directive = new NyxIdChatCanaryEffectFaultDirective
            {
                ArmId = command.ArmId.Trim(),
                ClientRequestId = command.ClientRequestId.Trim(),
                Key = command.Key.Clone(),
                ServiceInstanceId = command.ServiceInstanceId.Trim(),
                CatalogDigest = catalogDigest,
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

    public static NyxIdChatCanaryEffectFaultDirective? ForwardForPlanResolution(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanResolveCommand command,
        NyxIdChatOperationDispatchCommand dispatch,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(now);

        var arm = state.CanaryEffectFault;
        var directive = arm?.Directive;
        if (arm?.Status != NyxIdChatCanaryEffectFaultStatus.Armed || directive is null)
            return null;
        if (directive.ExpiresAt?.ToDateTimeOffset() <= now.ToDateTimeOffset())
        {
            arm.Status = NyxIdChatCanaryEffectFaultStatus.Expired;
            return null;
        }

        var continuation = dispatch.PlanGateContinuation;
        var operation = continuation?.OperationAdmission;
        var safety = EffectSafety(continuation?.MayChangeExternalState == true);
        if (!string.Equals(state.OwnerSubject, directive.OwnerSubject, StringComparison.Ordinal) ||
            !string.Equals(command.OwnerSubject, directive.OwnerSubject, StringComparison.Ordinal) ||
            !string.Equals(
                continuation?.ToolContext?.Caller?.OwnerSubject,
                directive.OwnerSubject,
                StringComparison.Ordinal) ||
            directive.Key is null ||
            !directive.Key.Equals(dispatch.Key) ||
            directive.Key.OperationGeneration != 1 ||
            operation?.ReadBack is null ||
            !string.Equals(operation.ServiceInstanceId, directive.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(operation.CatalogDigest, directive.CatalogDigest, StringComparison.Ordinal) ||
            !NyxIdChatOperationAdmissionPolicy.IsValid(operation, safety))
        {
            return null;
        }

        arm.Status = NyxIdChatCanaryEffectFaultStatus.Forwarded;
        arm.ForwardedAt = now.Clone();
        return directive.Clone();
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
        Timestamp now)
    {
        var continuation = command.PlanGateContinuation;
        var operation = continuation?.OperationAdmission;
        var safety = EffectSafety(continuation?.MayChangeExternalState == true);
        return directive?.Key is not null &&
               directive.Key.Equals(command.Key) &&
               directive.Key.OperationGeneration == 1 &&
               directive.ExpiresAt?.ToDateTimeOffset() > now.ToDateTimeOffset() &&
               continuation?.MayChangeExternalState == true &&
               operation?.ReadBack is not null &&
               string.Equals(
                   continuation.ToolContext?.Caller?.OwnerSubject,
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
        Timestamp now,
        out string catalogDigest)
    {
        catalogDigest = string.Empty;
        var expiresAt = command.ExpiresAt?.ToDateTimeOffset();
        var nowValue = now.ToDateTimeOffset();
        return command.ExpectedStateVersion == stateVersion &&
               !string.IsNullOrWhiteSpace(command.ArmId) &&
               !string.IsNullOrWhiteSpace(command.ClientRequestId) &&
               !string.IsNullOrWhiteSpace(command.ScopeId) &&
               !string.IsNullOrWhiteSpace(command.ConversationActorId) &&
               !string.IsNullOrWhiteSpace(command.ServiceInstanceId) &&
               !string.IsNullOrWhiteSpace(command.OwnerSubject) &&
               command.Key is not null &&
               HasCompleteKey(command.Key) &&
               command.Key.OperationGeneration == 1 &&
               string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
               string.Equals(state.ConversationActorId, command.ConversationActorId.Trim(),
                   StringComparison.Ordinal) &&
               string.Equals(state.ConversationActorId, command.Key.ConversationActorId,
                   StringComparison.Ordinal) &&
               string.Equals(state.OwnerSubject, command.OwnerSubject.Trim(), StringComparison.Ordinal) &&
               expiresAt > nowValue &&
               expiresAt <= nowValue + MaximumArmLifetime &&
               MatchesPendingEffect(state, command, out catalogDigest);
    }

    private static bool MatchesPendingEffect(
        NyxIdChatConversationGAgentState state,
        NyxIdChatCanaryEffectFaultArmCommand command,
        out string catalogDigest)
    {
        catalogDigest = string.Empty;
        var task = state.ActiveTask;
        var gate = task?.Gate;
        var key = command.Key;
        if (state.ActiveTurn is not
            {
                Status: NyxIdChatTurnStatus.Active,
            } activeTurn ||
            task is null ||
            task.Status != NyxIdChatTaskStatus.Active ||
            gate is not
            {
                Mode: NyxIdChatPlanGateMode.Confirm,
                Status: NyxIdChatPlanGateStatus.Pending,
            } ||
            gate.Admissions.Count != 1 ||
            key is null ||
            !string.Equals(activeTurn.TurnId, key.TurnId, StringComparison.Ordinal) ||
            !string.Equals(activeTurn.TaskId, key.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.TaskId, key.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.TurnId, key.TurnId, StringComparison.Ordinal) ||
            !string.Equals(task.TaskId, gate.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.PlanId, gate.PlanId, StringComparison.Ordinal) ||
            task.PlanRevision != gate.PlanRevision)
        {
            return false;
        }

        var gateAdmission = gate.Admissions[0];
        var turnAdmission = state.ActiveTurnPlanGateAdmission;
        var steps = task.Steps
            .Where(candidate =>
                candidate.Status == NyxIdChatStepStatus.Planned &&
                candidate.Kind == NyxIdChatStepKind.Tool &&
                candidate.MayChangeExternalState &&
                string.Equals(candidate.StepId, key.StepId, StringComparison.Ordinal) &&
                candidate.Operation?.Key?.Equals(key) == true)
            .Take(2)
            .ToArray();
        if (steps.Length != 1)
            return false;

        if (steps[0] is not { Source.Tool: { } stepTool } step)
            return false;

        var operation = stepTool.OperationAdmission;
        var matches = gateAdmission.Key?.Equals(key) == true &&
               string.IsNullOrWhiteSpace(gateAdmission.ActionRequestId) &&
               !string.IsNullOrWhiteSpace(gateAdmission.ToolCallId) &&
               !string.IsNullOrWhiteSpace(gateAdmission.ToolName) &&
               !gateAdmission.ArgumentsSha256.IsEmpty &&
               turnAdmission?.Key?.Equals(key) == true &&
               string.Equals(turnAdmission.GateRequestId, gate.RequestId, StringComparison.Ordinal) &&
               string.Equals(turnAdmission.TaskId, gate.TaskId, StringComparison.Ordinal) &&
               string.Equals(turnAdmission.PlanId, gate.PlanId, StringComparison.Ordinal) &&
               turnAdmission.PlanRevision == gate.PlanRevision &&
               string.Equals(
                   turnAdmission.ToolCallId,
                   gateAdmission.ToolCallId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   turnAdmission.ToolName,
                   gateAdmission.ToolName,
                   StringComparison.Ordinal) &&
               turnAdmission.ArgumentsSha256.Equals(gateAdmission.ArgumentsSha256) &&
               string.Equals(
                   stepTool.ToolName,
                   gateAdmission.ToolName,
                   StringComparison.Ordinal) &&
               turnAdmission.MayChangeExternalState &&
               turnAdmission.OperationAdmission?.Equals(operation) == true &&
               operation?.ReadBack is not null &&
               string.Equals(operation.ServiceInstanceId, command.ServiceInstanceId.Trim(),
                   StringComparison.Ordinal) &&
               IsCatalogDigest(operation.CatalogDigest) &&
               NyxIdChatOperationAdmissionPolicy.IsValid(operation, EffectSafety(true));
        if (matches)
            catalogDigest = operation!.CatalogDigest;
        return matches;
    }

    private static NyxIdChatToolCallSafety EffectSafety(bool mayChangeExternalState) => new()
    {
        IsReadOnly = false,
        IsDestructive = false,
        MayChangeExternalState = mayChangeExternalState,
    };

    private static bool IsExactReplay(
        NyxIdChatCanaryEffectFaultState? current,
        NyxIdChatCanaryEffectFaultArmCommand command)
    {
        var directive = current?.Directive;
        return directive is not null &&
               string.Equals(directive.ArmId, command.ArmId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(directive.ClientRequestId, command.ClientRequestId?.Trim(),
                   StringComparison.Ordinal) &&
               directive.Key?.Equals(command.Key) == true &&
               string.Equals(directive.ServiceInstanceId, command.ServiceInstanceId?.Trim(),
                   StringComparison.Ordinal) &&
               string.Equals(directive.OwnerSubject, command.OwnerSubject?.Trim(),
                   StringComparison.Ordinal) &&
               directive.ExpiresAt?.Equals(command.ExpiresAt) == true;
    }

    private static bool HasCompleteKey(NyxIdChatOperationKey key) =>
        !string.IsNullOrWhiteSpace(key.ConversationActorId) &&
        !string.IsNullOrWhiteSpace(key.TurnId) &&
        !string.IsNullOrWhiteSpace(key.TaskId) &&
        !string.IsNullOrWhiteSpace(key.StepId) &&
        !string.IsNullOrWhiteSpace(key.OperationId);

    private static bool IsCatalogDigest(string? value)
    {
        const string prefix = "sha256:";
        return value is not null &&
               value.Length == prefix.Length + 64 &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }
}
