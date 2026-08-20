using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatPostconditionContractUpgradeFailure
{
    internal const string Code =
        "NYXID_CHAT_POSTCONDITION_CONTRACT_UPGRADE_FAILED";
    internal const string SafeMessage =
        "The stored action verification cannot be upgraded safely.";

    internal static NyxIdChatConversationGAgentState BuildState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(now);

        var failed = state.Clone();
        var matchingSteps = failed.ActiveTask?.Steps
            .Where(candidate => KeysEqual(candidate.Operation?.Key, key))
            .Take(2)
            .ToArray() ?? [];
        var step = matchingSteps.Length == 1 ? matchingSteps[0] : null;
        if (step is not null)
        {
            step.Status = NyxIdChatStepStatus.Failed;
            step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
            step.FailureCode = Code;
            step.SafeMessage = SafeMessage;
            step.UpdatedAt = now.Clone();
            if (step.Operation is not null)
            {
                step.Operation.Phase = NyxIdChatOperationPhase.Failed;
                step.Operation.CompletedAt = now.Clone();
                step.Operation.TerminalCode = Code;
                step.Operation.SafeMessage = SafeMessage;
            }
            step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        }

        if (failed.ActiveTask is not null)
        {
            failed.ActiveTask.Status = NyxIdChatTaskStatus.Failed;
            failed.ActiveTask.ActiveStepId = string.Empty;
            failed.ActiveTask.ActiveOperationId = string.Empty;
            failed.ActiveTask.FailureCode = Code;
            failed.ActiveTask.SafeMessage = SafeMessage;
            failed.ActiveTask.UpdatedAt = now.Clone();
        }
        if (failed.ActiveTurn is not null)
        {
            failed.ActiveTurn.Status = NyxIdChatTurnStatus.Failed;
            failed.ActiveTurn.FailureCode = Code;
            failed.ActiveTurn.SafeMessage = SafeMessage;
            failed.ActiveTurn.TerminalAt = now.Clone();
            failed.LatestTurn = failed.ActiveTurn.Clone();
            NyxIdChatTaskLifecycle.AddTerminalSummary(failed, failed.ActiveTurn);
        }
        failed.ProgressSequence = checked(state.ProgressSequence + 1);
        failed.UpdatedAt = now.Clone();
        return failed;
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
}
