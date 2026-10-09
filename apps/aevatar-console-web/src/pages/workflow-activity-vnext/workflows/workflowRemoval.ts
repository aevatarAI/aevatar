import {
  ensureWorkflowObservationActive,
  waitForWorkflowObservation,
} from './workflowObservation';

export const WORKFLOW_REMOVAL_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

export type WorkflowRemovalObservationResult =
  | { readonly kind: 'observed' }
  | { readonly kind: 'delayed' };

export async function observeWorkflowRemoval(input: {
  readonly delaysMs?: readonly number[];
  readonly signal?: AbortSignal;
  readonly readWorkflows: () => Promise<readonly { workflowId: string }[]>;
  readonly wait?: (delayMs: number) => Promise<void>;
  readonly workflowId: string;
}): Promise<WorkflowRemovalObservationResult> {
  const delays = input.delaysMs ?? WORKFLOW_REMOVAL_OBSERVATION_DELAYS_MS;
  const wait =
    input.wait ??
    ((delayMs: number) => waitForWorkflowObservation(delayMs, input.signal));
  const workflowId = input.workflowId.trim();

  for (const delayMs of delays) {
    ensureWorkflowObservationActive(input.signal);
    if (delayMs > 0) await wait(delayMs);
    ensureWorkflowObservationActive(input.signal);
    const workflows = await input.readWorkflows();
    ensureWorkflowObservationActive(input.signal);
    if (!workflows.some((workflow) => workflow.workflowId === workflowId)) {
      return { kind: 'observed' };
    }
  }

  return { kind: 'delayed' };
}
