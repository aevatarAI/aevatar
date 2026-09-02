export const WORKFLOW_REMOVAL_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

export type WorkflowRemovalObservationResult =
  | { readonly kind: 'observed' }
  | { readonly kind: 'delayed' };

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

export async function observeWorkflowRemoval(input: {
  readonly delaysMs?: readonly number[];
  readonly readWorkflows: () => Promise<readonly { workflowId: string }[]>;
  readonly wait?: (delayMs: number) => Promise<void>;
  readonly workflowId: string;
}): Promise<WorkflowRemovalObservationResult> {
  const delays = input.delaysMs ?? WORKFLOW_REMOVAL_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;
  const workflowId = input.workflowId.trim();

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);
    const workflows = await input.readWorkflows();
    if (!workflows.some((workflow) => workflow.workflowId === workflowId)) {
      return { kind: 'observed' };
    }
  }

  return { kind: 'delayed' };
}
