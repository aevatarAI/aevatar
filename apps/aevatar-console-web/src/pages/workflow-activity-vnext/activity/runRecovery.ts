export type RecoveryStepCandidate = {
  readonly stepId: string;
  readonly success: boolean | null;
};

export type RecoveryGraphCandidate = {
  readonly nodes: readonly {
    readonly nodeId: string;
    readonly stepId: string;
  }[];
  readonly rootNodeId: string;
};

export function resolveRunRecovery(
  steps: readonly RecoveryStepCandidate[],
  graph?: RecoveryGraphCandidate,
): {
  readonly retryStepId: string | null;
  readonly runAgainStepId: string | null;
} {
  const failed = steps.filter(
    (step) => step.success === false && step.stepId.trim(),
  );
  const rootNodeId = graph?.rootNodeId.trim() ?? '';
  const rootStepId = rootNodeId
    ? (graph?.nodes
        .find((node) => node.nodeId.trim() === rootNodeId)
        ?.stepId.trim() ?? '')
    : '';
  const explicitRootStepId =
    rootStepId && steps.some((step) => step.stepId.trim() === rootStepId)
      ? rootStepId
      : null;
  return {
    retryStepId: failed.length === 1 ? failed[0].stepId : null,
    runAgainStepId: explicitRootStepId,
  };
}
