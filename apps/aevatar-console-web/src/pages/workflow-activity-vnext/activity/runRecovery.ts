import type {
  WorkflowActivityRunRecovery,
  WorkflowActivityRunRecoveryAction,
} from '@/shared/models/workflowActivity';

function resolveAction(
  action: WorkflowActivityRunRecoveryAction | undefined,
): WorkflowActivityRunRecoveryAction | null {
  return action?.eligible && action.startAtStepId?.trim() ? action : null;
}

export function resolveRunRecovery(
  recovery: WorkflowActivityRunRecovery | null | undefined,
): {
  readonly retryAction: WorkflowActivityRunRecoveryAction | null;
  readonly retryUnavailableReason: string | null;
  readonly runAgainAction: WorkflowActivityRunRecoveryAction | null;
  readonly runAgainUnavailableReason: string | null;
} {
  return {
    retryAction: resolveAction(recovery?.retry),
    retryUnavailableReason:
      recovery?.retry.eligible === false
        ? recovery.retry.unavailableReason.trim() || null
        : null,
    runAgainAction: resolveAction(recovery?.runAgain),
    runAgainUnavailableReason:
      recovery?.runAgain.eligible === false
        ? recovery.runAgain.unavailableReason.trim() || null
        : null,
  };
}
