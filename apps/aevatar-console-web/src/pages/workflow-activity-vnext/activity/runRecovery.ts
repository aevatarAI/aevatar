import type {
  WorkflowRecoveryActionCapability,
  WorkflowRecoveryRecommendedAction,
  WorkflowRunRecoveryCapability,
} from '@/shared/models/workflowActivity';

export interface RecoveryActionPresentation {
  readonly enabled: boolean;
  readonly mayIncurModelOrToolCost: boolean;
  readonly reason: string;
  readonly recommendedActions: readonly WorkflowRecoveryRecommendedAction[];
  readonly reusesPriorStepOutputs: boolean;
  readonly startingStepId: string;
}

export interface RunRecoveryPresentation {
  readonly retry: RecoveryActionPresentation;
  readonly runAgain: RecoveryActionPresentation;
  readonly workflowDefinitionRevisionId: string;
  readonly workflowDefinitionVersion: number;
}

function resolveAction(
  capability: WorkflowRecoveryActionCapability,
): RecoveryActionPresentation {
  const startingStepId = capability.startingStepId.trim();
  const eligible = capability.eligibility === 1;
  return {
    enabled: eligible && Boolean(startingStepId),
    mayIncurModelOrToolCost: capability.mayIncurModelOrToolCost,
    reason:
      capability.unavailableReason.trim() ||
      (eligible && !startingStepId
        ? 'Recovery starting step is unavailable.'
        : ''),
    recommendedActions: capability.recommendedActions,
    reusesPriorStepOutputs: capability.reusesPriorStepOutputs,
    startingStepId,
  };
}

export function resolveRunRecovery(
  capability: WorkflowRunRecoveryCapability,
): RunRecoveryPresentation {
  return {
    retry: resolveAction(capability.retryFailedStep),
    runAgain: resolveAction(capability.runAgain),
    workflowDefinitionRevisionId:
      capability.workflowDefinitionRevisionId.trim(),
    workflowDefinitionVersion: capability.workflowDefinitionVersion,
  };
}
