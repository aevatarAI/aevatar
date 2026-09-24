import React from 'react';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import type {
  StudioWorkflowDraftCreateAcceptedReceipt,
  StudioWorkflowFile,
} from '@/shared/studio/models';

export const DRAFT_MATERIALIZATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000, 3000, 5000,
] as const;

type ObservationInput<T> = {
  readonly delaysMs?: readonly number[];
  readonly isNotFound: (error: unknown) => boolean;
  readonly isObserved?: (workflow: T) => boolean;
  readonly read: (workflowId: string) => Promise<T | null>;
  readonly wait?: (delayMs: number) => Promise<void>;
  readonly workflowId: string;
};

export type DraftMaterializationResult<T> =
  | { readonly kind: 'readable'; readonly workflow: T }
  | { readonly kind: 'delayed' };

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

export async function observeDraftMaterialization<T>(
  input: ObservationInput<T>,
): Promise<DraftMaterializationResult<T>> {
  const delays = input.delaysMs ?? DRAFT_MATERIALIZATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);
    try {
      const workflow = await input.read(input.workflowId);
      if (
        workflow !== null &&
        (!input.isObserved || input.isObserved(workflow))
      ) {
        return { kind: 'readable', workflow };
      }
    } catch (error) {
      if (!input.isNotFound(error)) throw error;
    }
  }

  return { kind: 'delayed' };
}

export async function readWorkflowDraftAfterList(
  workflowId: string,
  scopeId: string,
): Promise<StudioWorkflowFile | null> {
  const drafts = await studioApi.listWorkflowDrafts(scopeId);
  if (!drafts.some((draft) => draft.workflowId === workflowId)) return null;

  return studioApi.getWorkflowDraftFile(workflowId, scopeId);
}

export type DraftMaterializationPhase =
  | 'idle'
  | 'accepted'
  | 'observing'
  | 'delayed'
  | 'readable'
  | 'failed';

export function useDraftMaterialization(scopeId: string) {
  const [phase, setPhase] = React.useState<DraftMaterializationPhase>('idle');
  const [receipt, setReceipt] =
    React.useState<StudioWorkflowDraftCreateAcceptedReceipt | null>(null);
  const [error, setError] = React.useState<unknown>(null);
  const generationRef = React.useRef(0);

  const observe = React.useCallback(
    async (
      nextReceipt: StudioWorkflowDraftCreateAcceptedReceipt,
    ): Promise<StudioWorkflowFile | null> => {
      const generation = ++generationRef.current;
      setReceipt(nextReceipt);
      setError(null);
      setPhase('accepted');
      await Promise.resolve();
      if (generation !== generationRef.current) return null;
      setPhase('observing');

      try {
        const result = await observeDraftMaterialization({
          workflowId: nextReceipt.workflowId,
          read: (workflowId) => readWorkflowDraftAfterList(workflowId, scopeId),
          isNotFound: (candidate) => isStudioApiStatus(candidate, 404),
        });
        if (generation !== generationRef.current) return null;
        if (result.kind === 'delayed') {
          setPhase('delayed');
          return null;
        }
        setPhase('readable');
        return result.workflow;
      } catch (candidate) {
        if (generation !== generationRef.current) return null;
        setError(candidate);
        setPhase('failed');
        return null;
      }
    },
    [scopeId],
  );

  const retry = React.useCallback(
    () => (receipt ? observe(receipt) : Promise.resolve(null)),
    [observe, receipt],
  );

  const reset = React.useCallback(() => {
    generationRef.current += 1;
    setError(null);
    setPhase('idle');
    setReceipt(null);
  }, []);

  React.useEffect(
    () => () => {
      generationRef.current += 1;
    },
    [],
  );

  return { error, observe, phase, receipt, reset, retry } as const;
}
