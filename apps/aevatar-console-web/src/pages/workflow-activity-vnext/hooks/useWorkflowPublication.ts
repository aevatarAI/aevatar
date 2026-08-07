import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
import type { StudioMemberBindingRunStatusResponse } from '@/shared/studio/models';

export const WORKFLOW_PUBLICATION_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

const ACTIVE_BINDING_RUN_STATUSES = new Set([
  'accepted',
  'admission_pending',
  'admitted',
  'platform_binding_pending',
  'member_notification_pending',
]);

export type WorkflowPublicationReceipt = {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly memberId: string;
  readonly bindingRunId: string;
  readonly revisionId: string;
};

export type WorkflowPublicationObservationInput = {
  readonly delaysMs?: readonly number[];
  readonly readBindingRun: (
    scopeId: string,
    memberId: string,
    bindingRunId: string,
  ) => Promise<StudioMemberBindingRunStatusResponse>;
  readonly receipt: WorkflowPublicationReceipt;
  readonly wait?: (delayMs: number) => Promise<void>;
};

export type WorkflowPublicationObservationResult =
  | {
      readonly kind: 'observed';
      readonly publishedServiceId: string;
      readonly run: StudioMemberBindingRunStatusResponse;
    }
  | { readonly kind: 'delayed' };

export type WorkflowPublicationObservationPhase =
  | 'idle'
  | 'observing'
  | 'observed'
  | 'delayed'
  | 'unauthorized'
  | 'forbidden'
  | 'failed';

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

function statusOf(error: unknown): number | undefined {
  if (
    error &&
    typeof error === 'object' &&
    'status' in error &&
    typeof error.status === 'number'
  ) {
    return error.status;
  }

  return undefined;
}

function assertAcceptedBindingRunIdentity(
  receipt: WorkflowPublicationReceipt,
  run: StudioMemberBindingRunStatusResponse,
): void {
  if (
    run.scopeId !== receipt.scopeId ||
    run.memberId !== receipt.memberId ||
    run.bindingRunId !== receipt.bindingRunId
  ) {
    throw new Error(
      'The observed binding run does not match the accepted publication.',
    );
  }
}

function readBindingRunFailureMessage(
  run: StudioMemberBindingRunStatusResponse,
): string {
  return (
    run.failure?.message?.trim() ||
    run.failure?.code.trim() ||
    'The accepted workflow publication failed.'
  );
}

export async function observeWorkflowPublication(
  input: WorkflowPublicationObservationInput,
): Promise<WorkflowPublicationObservationResult> {
  const delays = input.delaysMs ?? WORKFLOW_PUBLICATION_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);

    let run: StudioMemberBindingRunStatusResponse;
    try {
      run = await input.readBindingRun(
        input.receipt.scopeId,
        input.receipt.memberId,
        input.receipt.bindingRunId,
      );
    } catch (error) {
      if (statusOf(error) === 404) continue;
      throw error;
    }

    assertAcceptedBindingRunIdentity(input.receipt, run);

    if (run.status === 'failed' || run.status === 'rejected') {
      throw new Error(readBindingRunFailureMessage(run));
    }
    if (ACTIVE_BINDING_RUN_STATUSES.has(run.status)) continue;
    if (run.status !== 'succeeded') continue;

    const publishedServiceId = run.result?.publishedServiceId.trim() ?? '';
    if (!publishedServiceId) {
      throw new Error(
        'The succeeded binding run did not provide a published service identity.',
      );
    }
    if (run.result?.revisionId !== input.receipt.revisionId) {
      throw new Error(
        'The succeeded binding run does not match the accepted revision.',
      );
    }

    return { kind: 'observed', publishedServiceId, run };
  }

  return { kind: 'delayed' };
}

export function resolveWorkflowPublicationPhase(input: {
  readonly data: WorkflowPublicationObservationResult | undefined;
  readonly enabled: boolean;
  readonly error: unknown;
  readonly isFetching: boolean;
  readonly isPending: boolean;
}): WorkflowPublicationObservationPhase {
  if (!input.enabled) return 'idle';
  if (input.isPending || input.isFetching) return 'observing';

  const status = statusOf(input.error);
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (input.error) return 'failed';
  if (input.data?.kind === 'observed') return 'observed';
  if (input.data?.kind === 'delayed') return 'delayed';
  return 'failed';
}

export function useWorkflowPublication(
  receipt: WorkflowPublicationReceipt | null,
) {
  const enabled = receipt !== null;
  const query = useQuery({
    enabled,
    queryKey: [
      'workflow-activity-vnext',
      'workflow-publication',
      receipt?.scopeId ?? '',
      receipt?.memberId ?? '',
      receipt?.bindingRunId ?? '',
      receipt?.revisionId ?? '',
    ],
    queryFn: () => {
      if (!receipt) {
        throw new Error('A workflow publication receipt is required.');
      }

      return observeWorkflowPublication({
        receipt,
        readBindingRun: (scopeId, memberId, bindingRunId) =>
          studioApi.getMemberBindingRun(scopeId, memberId, bindingRunId),
      });
    },
    retry: false,
  });
  const phase = resolveWorkflowPublicationPhase({
    data: query.data,
    enabled,
    error: query.error,
    isFetching: query.isFetching,
    isPending: query.isPending,
  });

  const retry = React.useCallback(async () => {
    if (!enabled) return null;
    const result = await query.refetch();
    return result.data ?? null;
  }, [enabled, query]);

  return {
    error: query.error,
    phase,
    receipt,
    publishedServiceId:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.publishedServiceId
        : '',
    retry,
    run:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.run
        : null,
  } as const;
}
