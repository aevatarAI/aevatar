export type WorkflowArchivalFacts = {
  readonly activeRevisionId: string;
  readonly deploymentId: string;
  readonly deploymentStatus: string;
  readonly hasCommittedSource: boolean;
};

export type WorkflowArchivalObservationItem = WorkflowArchivalFacts & {
  readonly workflowId: string;
};

export const WORKFLOW_ARCHIVAL_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

export type WorkflowArchivalObservationResult =
  | {
      readonly kind: 'observed';
      readonly workflows: readonly WorkflowArchivalObservationItem[];
    }
  | { readonly kind: 'delayed' };

function normalizeDeploymentStatus(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[\s_-]+/g, '');
}

export function isWorkflowArchived(
  workflow: Pick<WorkflowArchivalFacts, 'deploymentStatus'>,
): boolean {
  return normalizeDeploymentStatus(workflow.deploymentStatus) === 'deactivated';
}

export function canArchiveWorkflow(workflow: WorkflowArchivalFacts): boolean {
  return (
    workflow.hasCommittedSource &&
    Boolean(workflow.activeRevisionId.trim()) &&
    Boolean(workflow.deploymentId.trim()) &&
    normalizeDeploymentStatus(workflow.deploymentStatus) === 'active'
  );
}

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

export async function observeWorkflowArchival(input: {
  readonly delaysMs?: readonly number[];
  readonly readWorkflows: () => Promise<
    readonly WorkflowArchivalObservationItem[]
  >;
  readonly wait?: (delayMs: number) => Promise<void>;
  readonly workflowId: string;
}): Promise<WorkflowArchivalObservationResult> {
  const delays = input.delaysMs ?? WORKFLOW_ARCHIVAL_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);
    const workflows = await input.readWorkflows();
    const target = workflows.find(
      (workflow) => workflow.workflowId === input.workflowId,
    );
    if (target && isWorkflowArchived(target)) {
      return { kind: 'observed', workflows };
    }
  }

  return { kind: 'delayed' };
}
